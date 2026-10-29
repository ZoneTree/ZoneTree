using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments;

namespace ZoneTree.Transactional;

/// <summary>
/// A transactional ZoneTree that stores every version of a key in a single storage tree,
/// with multi-version snapshot reads and optimistic, commit-time conflict detection.
/// </summary>
/// <remarks>
/// <para>
/// One clock issues transaction ids and commit timestamps. A transaction reads the
/// snapshot of the latest commit timestamp when it begins. Its writes are stored at once
/// under its id, so they persist and merge like any other record, and they stay invisible
/// to others until the commit.
/// </para>
/// <para>
/// A transaction that writes stores a pending status record before its first version.
/// The commit deletes that record: it is the single write that makes the transaction
/// committed. After a crash, a remaining status record means the transaction never
/// committed. The storage tree syncs each segment's WAL before the next segment accepts
/// writes, so a recovered commit always comes with every write made before it.
/// </para>
/// <para>
/// Two committed transactions that wrote the same key never overlap, because the later
/// one would conflict. So among committed versions of a key, a higher transaction id is
/// a later commit, and the newest version a snapshot can see is its value.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class TransactionalZoneTree<TKey, TValue> :
    ITransactionalZoneTree<TKey, TValue>,
    ITransactionalZoneTreeMaintenance<TKey, TValue>
{
  /// <summary>
  /// The clock values reserved by one write of the clock record.
  /// A restart continues after the reserved values.
  /// </summary>
  const long ClockReservationSize = 1 << 20;

  const long PendingStatus = 1;

  readonly IRefComparer<TKey> Comparer;

  readonly ConflictIndex<TKey> ConflictIndex;

  readonly ConcurrentDictionary<long, TransactionEntry> Entries = new();

  readonly Lock StateLock = new();

  readonly Lock CommitLock = new();

  readonly Lock AutoSyncLock = new();

  // The fields below are guarded by StateLock.

  long Clock;

  long ReservedClock;

  long lastCommitTimestamp;

  int activeTransactionCount;

  readonly SortedDictionary<long, int> ActiveSnapshots = new();

  /// <summary>
  /// The snapshots of the transactions that can write. Only they check conflicts.
  /// </summary>
  readonly SortedDictionary<long, int> ActiveWriterSnapshots = new();

  readonly SortedDictionary<long, int> ActiveBeginSequences = new();

  /// <summary>
  /// Committed entries, in commit order, until every snapshot sees them.
  /// </summary>
  readonly Queue<TransactionEntry> CommittedTransactions = new();

  /// <summary>
  /// Committed keys, in commit order, until no writing transaction can conflict with them.
  /// </summary>
  readonly Queue<(long CommitTimestamp, TKey[] Keys)> CommittedWrites = new();

  /// <summary>
  /// Rolled back transactions whose versions may still be read.
  /// </summary>
  readonly List<TransactionEntry> AbortedTransactions = new();

  long BottomMarker;

  long LastMergedDiskSegmentId;

  TimeSpan autoSyncInterval;

  CancellationTokenSource AutoSyncCancellation;

  Task AutoSyncTask;

  bool isDisposed;

  internal IZoneTree<VersionedKey<TKey>, VersionedValue<TValue>> Storage { get; }

  IZoneTreeMaintenance<VersionedKey<TKey>, VersionedValue<TValue>> StorageMaintenance { get; }

  TransactionalZoneTree(
      IZoneTree<VersionedKey<TKey>, VersionedValue<TValue>> storage,
      IRefComparer<TKey> comparer)
  {
    Storage = storage;
    StorageMaintenance = storage.Maintenance;
    Comparer = comparer;
    ConflictIndex = new ConflictIndex<TKey>(comparer);
    Recover();
    BottomMarker = GetBottomMarker(0);
    StorageMaintenance.OnDiskSegmentCreated += OnDiskSegmentCreated;
    StorageMaintenance.OnMergeOperationEnded += OnMergeOperationEnded;
    StorageMaintenance.OnBottomSegmentsMergeOperationEnded += OnBottomSegmentsMergeOperationEnded;
  }

  /// <summary>
  /// Opens or creates a transactional tree with the given user options.
  /// </summary>
  internal static TransactionalZoneTree<TKey, TValue> Load(
      ZoneTreeOptions<TKey, TValue> options,
      bool canCreate,
      bool canOpen)
  {
    var owner = new StrongBox<TransactionalZoneTree<TKey, TValue>>();
    var storageOptions = CreateStorageOptions(options, owner);
    var loader = new ZoneTreeLoader<VersionedKey<TKey>, VersionedValue<TValue>>(storageOptions);
    ZoneTree<VersionedKey<TKey>, VersionedValue<TValue>> storage;
    if (loader.ZoneTreeMetaExists)
    {
      if (!canOpen)
        throw new DatabaseAlreadyExistsException();
      storage = loader.LoadZoneTree();
    }
    else
    {
      if (!canCreate)
        throw new DatabaseNotFoundException();
      storage = new ZoneTree<VersionedKey<TKey>, VersionedValue<TValue>>(storageOptions);
    }
    try
    {
      owner.Value = new TransactionalZoneTree<TKey, TValue>(storage, options.Comparer);
      return owner.Value;
    }
    catch
    {
      storage.Dispose();
      throw;
    }
  }

  static ZoneTreeOptions<VersionedKey<TKey>, VersionedValue<TValue>> CreateStorageOptions(
      ZoneTreeOptions<TKey, TValue> options,
      StrongBox<TransactionalZoneTree<TKey, TValue>> owner)
  {
    return new ZoneTreeOptions<VersionedKey<TKey>, VersionedValue<TValue>>
    {
      MutableSegmentMaxItemCount = options.MutableSegmentMaxItemCount,
      // Reads seek the versions of a key, so lookups by exact key never happen.
      MutableSegmentBloomFilterBitsPerItem = 0,
      DiskSegmentMaxItemCount = options.DiskSegmentMaxItemCount,
      Comparer = new VersionedKeyComparer<TKey>(options.Comparer),
      KeySerializer = new VersionedKeySerializer<TKey>(options.KeySerializer),
      ValueSerializer = new VersionedValueSerializer<TValue>(options.ValueSerializer),
      IsDeleted = static (in VersionedKey<TKey> _, in VersionedValue<TValue> value) =>
          value.Kind == VersionedValueKind.Deleted,
      MarkValueDeleted = static (ref VersionedValue<TValue> value) =>
          value = VersionedValue<TValue>.Deleted,
      WriteAheadLogOptions = options.WriteAheadLogOptions,
      DiskSegmentOptions = options.DiskSegmentOptions,
      BTreeLockMode = options.BTreeLockMode,
      BTreeNodeSize = options.BTreeNodeSize,
      BTreeLeafSize = options.BTreeLeafSize,
      AllowUnsafeOptionValues = options.AllowUnsafeOptionValues,
      Logger = options.Logger,
      WriteAheadLogProvider = options.WriteAheadLogProvider,
      RandomAccessDeviceManager = options.RandomAccessDeviceManager,
      EnableSingleSegmentGarbageCollection = options.EnableSingleSegmentGarbageCollection,
      PreserveWriteOrderAcrossSegments = true,
      MergeFilterFactory = includesOldestRecords =>
          owner.Value?.CreateMergeFilter(includesOldestRecords),
    };
  }

  /// <summary>
  /// Restores the clock and the transactions that never committed from the system records.
  /// </summary>
  void Recover()
  {
    using var iterator = Storage.CreateIterator(IteratorType.NoRefresh, false, false);
    iterator.SeekFirst();
    while (iterator.Next())
    {
      var key = iterator.CurrentKey;
      if (key.Kind == VersionedKeyKind.Data)
        break;
      if (key.Kind == VersionedKeyKind.Clock)
      {
        ReservedClock = iterator.CurrentValue.Meta;
        continue;
      }
      // The transaction did not commit before the tree closed. Its versions may be
      // anywhere in the tree, so it stays registered as rolled back.
      Entries[key.Version] = new TransactionEntry(key.Version, TransactionState.Aborted)
      {
        IsPermanent = true
      };
    }
    Clock = ReservedClock;
    lastCommitTimestamp = Clock;
  }

  public ITransaction<TKey, TValue> BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    lock (StateLock)
    {
      var snapshotTimestamp = lastCommitTimestamp;
      var id = NextTimestamp();
      var entry = new TransactionEntry(id, TransactionState.Active);
      Entries[id] = entry;
      Register(snapshotTimestamp, id, isWriter: true);
      return new Transaction<TKey, TValue>(
          this, Comparer, entry, id, snapshotTimestamp, isolationLevel);
    }
  }

  public ITransaction<TKey, TValue> BeginReadOnlyTransaction()
  {
    lock (StateLock)
    {
      var snapshotTimestamp = lastCommitTimestamp;
      Register(snapshotTimestamp, Clock, isWriter: false);
      return new Transaction<TKey, TValue>(
          this, Comparer, null, Clock, snapshotTimestamp, TransactionIsolationLevel.Snapshot);
    }
  }

  /// <summary>
  /// Issues the next clock value. Called under the state lock.
  /// </summary>
  long NextTimestamp()
  {
    var next = Clock + 1;
    if (next > ReservedClock)
    {
      // Persist the reservation before any id from it can reach the storage tree.
      var reserved = next + ClockReservationSize - 1;
      Storage.Upsert(VersionedKey<TKey>.Clock, VersionedValue<TValue>.FromSystem(reserved));
      ReservedClock = reserved;
    }
    Clock = next;
    return next;
  }

  void Register(long snapshotTimestamp, long beginSequence, bool isWriter)
  {
    Increment(ActiveSnapshots, snapshotTimestamp);
    if (isWriter)
      Increment(ActiveWriterSnapshots, snapshotTimestamp);
    Increment(ActiveBeginSequences, beginSequence);
    ++activeTransactionCount;
  }

  void Unregister(long snapshotTimestamp, long beginSequence, bool isWriter)
  {
    Decrement(ActiveSnapshots, snapshotTimestamp);
    if (isWriter)
      Decrement(ActiveWriterSnapshots, snapshotTimestamp);
    Decrement(ActiveBeginSequences, beginSequence);
    --activeTransactionCount;
  }

  static void Increment(SortedDictionary<long, int> counts, long key)
  {
    counts.TryGetValue(key, out var count);
    counts[key] = count + 1;
  }

  static void Decrement(SortedDictionary<long, int> counts, long key)
  {
    var count = counts[key] - 1;
    if (count == 0)
      counts.Remove(key);
    else
      counts[key] = count;
  }

  static long Minimum(SortedDictionary<long, int> counts, long defaultValue)
  {
    foreach (var key in counts.Keys)
      return key;
    return defaultValue;
  }

  /// <summary>
  /// The oldest snapshot timestamp in use. Every commit at or before it
  /// is visible to every active and future snapshot. Called under the state lock.
  /// </summary>
  long GetHorizon() => Minimum(ActiveSnapshots, lastCommitTimestamp);

  /// <summary>
  /// Checks whether a version is in the snapshot of a transaction.
  /// </summary>
  internal bool IsVisible(long version, Transaction<TKey, TValue> transaction)
  {
    // A version above the snapshot either is the transaction's own write or
    // belongs to a transaction that began later, so it commits after the snapshot.
    if (version > transaction.SnapshotTimestamp)
      return version == transaction.TransactionId;
    if (!Entries.TryGetValue(version, out var entry))
      return true;
    return entry.State == TransactionState.Committed &&
        entry.CommitTimestamp <= transaction.SnapshotTimestamp;
  }

  internal VersionState GetVersionState(long version, long horizon, out long commitTimestamp)
  {
    commitTimestamp = 0;
    if (!Entries.TryGetValue(version, out var entry))
      return VersionState.VisibleToAll;
    switch (entry.State)
    {
      case TransactionState.Aborted:
        return VersionState.Aborted;
      case TransactionState.Committed:
        commitTimestamp = entry.CommitTimestamp;
        return commitTimestamp <= horizon ?
            VersionState.VisibleToAll :
            VersionState.Committed;
      default:
        return VersionState.Active;
    }
  }

  internal bool HasCommitAfter(in TKey key, long timestamp)
      => ConflictIndex.HasCommitAfter(in key, timestamp);

  internal void OnFirstWrite(Transaction<TKey, TValue> transaction)
  {
    // Read the marker before the first write, so a later move of
    // a disk segment to the bottom segments is never missed.
    transaction.Entry.BottomMarker = Volatile.Read(ref BottomMarker);
    Storage.Upsert(
        VersionedKey<TKey>.TransactionStatus(transaction.TransactionId),
        VersionedValue<TValue>.FromSystem(PendingStatus));
  }

  internal IZoneTreeIterator<VersionedKey<TKey>, VersionedValue<TValue>> CreateStorageIterator(
      bool reverse,
      bool contributeToTheBlockCache,
      int diskSegmentPrefetchSize)
  {
    // The visibility check gives the snapshot, so the iterator only has to see
    // new segments: they hold the transaction's own later writes.
    var options = new IteratorOptions
    {
      IteratorType = IteratorType.AutoRefresh,
      ContributeToTheBlockCache = contributeToTheBlockCache,
      DiskSegmentPrefetchSize = diskSegmentPrefetchSize
    };
    return reverse ?
        Storage.CreateReverseIterator(options) :
        Storage.CreateIterator(options);
  }

  /// <summary>
  /// Commits the transaction, or rolls it back if it conflicts.
  /// </summary>
  internal bool Commit(Transaction<TKey, TValue> transaction, out long commitTimestamp)
  {
    var entry = transaction.Entry;
    if (!transaction.HasWrites)
    {
      // Nothing to publish: the transaction is equivalent to running at its snapshot.
      commitTimestamp = transaction.SnapshotTimestamp;
      if (entry != null)
        Entries.TryRemove(entry.Id, out _);
      EndTransaction(transaction);
      return true;
    }

    var hasConflict = false;
    commitTimestamp = 0;
    lock (CommitLock)
    {
      try
      {
        // Commits only happen under this lock, so no commit since the snapshot means no conflict.
        hasConflict =
            Volatile.Read(ref lastCommitTimestamp) > transaction.SnapshotTimestamp &&
            transaction.HasConflict(ConflictIndex);
        if (!hasConflict)
        {
          lock (StateLock)
            commitTimestamp = NextTimestamp();
          // The commit point: a single record, durable with the next WAL sync.
          Storage.ForceDelete(VersionedKey<TKey>.TransactionStatus(transaction.TransactionId));
        }
      }
      catch
      {
        RollbackCore(transaction);
        throw;
      }
      if (!hasConflict)
      {
        entry.CommitTimestamp = commitTimestamp;
        entry.State = TransactionState.Committed;
        var keys = transaction.GetWrittenKeys();
        ConflictIndex.Add(keys, commitTimestamp);
        lock (StateLock)
        {
          // New snapshots include the commit only after its entry says committed.
          Volatile.Write(ref lastCommitTimestamp, commitTimestamp);
          CommittedTransactions.Enqueue(entry);
          CommittedWrites.Enqueue((commitTimestamp, keys));
        }
      }
    }
    if (hasConflict)
    {
      RollbackCore(transaction);
      return false;
    }
    EndTransaction(transaction);
    return true;
  }

  internal void Rollback(Transaction<TKey, TValue> transaction)
  {
    RollbackCore(transaction);
  }

  void RollbackCore(Transaction<TKey, TValue> transaction)
  {
    try
    {
      var entry = transaction.Entry;
      if (entry == null)
        return;
      // Readers and merges ignore the versions from now on.
      entry.State = TransactionState.Aborted;
      if (!transaction.HasWrites)
      {
        Entries.TryRemove(entry.Id, out _);
        return;
      }
      try
      {
        foreach (var key in transaction.WrittenKeysForRollback)
          Storage.ForceDelete(VersionedKey<TKey>.Data(in key, transaction.TransactionId));
      }
      catch (Exception e)
      {
        // The entry and the pending status record stay, so the versions stay invisible.
        entry.IsPermanent = true;
        Logger.LogError(e);
        return;
      }
      lock (StateLock)
      {
        entry.AbortSegmentId = StorageMaintenance.MutableSegment.SegmentId;
        entry.AbortSequence = Clock;
        AbortedTransactions.Add(entry);
      }
    }
    finally
    {
      EndTransaction(transaction);
    }
  }

  void EndTransaction(Transaction<TKey, TValue> transaction)
  {
    lock (StateLock)
    {
      Unregister(
          transaction.SnapshotTimestamp,
          transaction.BeginSequence,
          isWriter: !transaction.IsReadOnly);
      PruneCommittedTransactions();
      RemoveReleasedAbortedTransactions();
    }
  }

  /// <summary>
  /// Forgets the commits every snapshot sees, and the committed keys no writing
  /// transaction can conflict with. Called under the state lock.
  /// </summary>
  void PruneCommittedTransactions()
  {
    if (CommittedTransactions.Count > 0)
    {
      var horizon = GetHorizon();
      while (CommittedTransactions.TryPeek(out var entry) &&
          entry.CommitTimestamp <= horizon)
      {
        CommittedTransactions.Dequeue();
        Entries.TryRemove(entry.Id, out _);
      }
    }
    if (CommittedWrites.Count > 0)
    {
      // Read-only transactions never check conflicts, so a long report
      // does not keep the committed keys.
      var writerHorizon = Minimum(ActiveWriterSnapshots, lastCommitTimestamp);
      while (CommittedWrites.TryPeek(out var item) &&
          item.CommitTimestamp <= writerHorizon)
      {
        CommittedWrites.Dequeue();
        ConflictIndex.Remove(item.Keys, item.CommitTimestamp);
      }
    }
  }

  /// <summary>
  /// Forgets rolled back transactions whose versions no reader or merge can meet.
  /// Called under the state lock.
  /// </summary>
  void RemoveReleasedAbortedTransactions()
  {
    if (AbortedTransactions.Count == 0)
      return;
    var oldestBeginSequence = Minimum(ActiveBeginSequences, long.MaxValue);
    for (var i = AbortedTransactions.Count - 1; i >= 0; --i)
    {
      var entry = AbortedTransactions[i];
      // A transaction that began before the deletions may hold an iterator over
      // segments that still have the versions without the deletions.
      if (!entry.AreDeletionsMerged || oldestBeginSequence <= entry.AbortSequence)
        continue;
      try
      {
        Storage.ForceDelete(VersionedKey<TKey>.TransactionStatus(entry.Id));
      }
      catch (Exception e)
      {
        Logger.LogError(e);
        continue;
      }
      Entries.TryRemove(entry.Id, out _);
      AbortedTransactions.RemoveAt(i);
    }
  }

  internal IMergeFilter<VersionedKey<TKey>, VersionedValue<TValue>> CreateMergeFilter(
      bool includesOldestRecords)
  {
    long[] snapshots;
    lock (StateLock)
    {
      // Snapshots that begin during the merge read the latest commit or a later one.
      snapshots = new long[ActiveSnapshots.Count + 1];
      ActiveSnapshots.Keys.CopyTo(snapshots, 0);
      snapshots[^1] = lastCommitTimestamp;
    }
    return new VersionGarbageFilter<TKey, TValue>(this, Comparer, includesOldestRecords, snapshots);
  }

  /// <summary>
  /// The highest bottom segment id, excluding the given one. It changes whenever
  /// a disk segment moves to the bottom segments or bottom segments merge.
  /// </summary>
  long GetBottomMarker(long excludedSegmentId)
  {
    long marker = 0;
    foreach (var segment in StorageMaintenance.BottomSegments)
    {
      var id = segment.SegmentId;
      if (id != excludedSegmentId && id > marker)
        marker = id;
    }
    return marker;
  }

  void OnDiskSegmentCreated(
      IZoneTreeMaintenance<VersionedKey<TKey>, VersionedValue<TValue>> zoneTree,
      IDiskSegment<VersionedKey<TKey>, VersionedValue<TValue>> newDiskSegment,
      bool isBottomSegment)
  {
    if (!isBottomSegment)
      Volatile.Write(ref LastMergedDiskSegmentId, newDiskSegment.SegmentId);
  }

  void OnBottomSegmentsMergeOperationEnded(
      IZoneTreeMaintenance<VersionedKey<TKey>, VersionedValue<TValue>> zoneTree,
      MergeResult mergeResult)
  {
    Volatile.Write(ref BottomMarker, GetBottomMarker(0));
  }

  /// <summary>
  /// Decides which rolled back transactions had their deletions merged.
  /// </summary>
  /// <remarks>
  /// A merge that takes the segment holding the deletions also takes every newer
  /// segment's older copies and the disk segment, so the deletions remove every
  /// version. Only versions that reached the bottom segments before that merge can
  /// survive. That needs a disk segment to move to the bottom segments between the
  /// first write and the merge, and then the transaction stays registered for good.
  /// </remarks>
  void OnMergeOperationEnded(
      IZoneTreeMaintenance<VersionedKey<TKey>, VersionedValue<TValue>> zoneTree,
      MergeResult mergeResult)
  {
    var markerBeforeMerge = GetBottomMarker(Volatile.Read(ref LastMergedDiskSegmentId));
    Volatile.Write(ref BottomMarker, GetBottomMarker(0));
    if (mergeResult != MergeResult.SUCCESS)
      return;
    lock (StateLock)
    {
      if (AbortedTransactions.Count == 0)
        return;
      for (var i = AbortedTransactions.Count - 1; i >= 0; --i)
      {
        var entry = AbortedTransactions[i];
        if (entry.AreDeletionsMerged || !IsSegmentMerged(entry.AbortSegmentId))
          continue;
        if (entry.BottomMarker != markerBeforeMerge)
        {
          entry.IsPermanent = true;
          AbortedTransactions.RemoveAt(i);
          continue;
        }
        entry.AreDeletionsMerged = true;
      }
      RemoveReleasedAbortedTransactions();
    }
  }

  /// <summary>
  /// True if the segment and every older in-memory segment were merged into the disk segment.
  /// </summary>
  bool IsSegmentMerged(long segmentId)
  {
    if (StorageMaintenance.MutableSegment.SegmentId <= segmentId)
      return false;
    foreach (var segment in StorageMaintenance.ReadOnlySegments)
    {
      if (segment.SegmentId <= segmentId)
        return false;
    }
    return true;
  }

  public Task SyncWALAsync(CancellationToken cancellationToken = default)
      => Storage.SyncWALAsync(cancellationToken);

  public TimeSpan AutoSyncInterval
  {
    get => autoSyncInterval;
    set
    {
      ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
      lock (AutoSyncLock)
      {
        StopAutoSync();
        autoSyncInterval = value;
        if (value > TimeSpan.Zero && !isDisposed)
          StartAutoSync(value);
      }
    }
  }

  void StartAutoSync(TimeSpan interval)
  {
    var cancellation = new CancellationTokenSource();
    AutoSyncCancellation = cancellation;
    AutoSyncTask = Task.Run(async () =>
    {
      using var timer = new PeriodicTimer(interval);
      try
      {
        while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
        {
          try
          {
            await Storage.SyncWALAsync(cancellation.Token).ConfigureAwait(false);
          }
          catch (OperationCanceledException)
          {
            return;
          }
          catch (Exception e)
          {
            Logger.LogError(e);
          }
        }
      }
      catch (OperationCanceledException)
      {
      }
    });
  }

  void StopAutoSync()
  {
    var cancellation = AutoSyncCancellation;
    if (cancellation == null)
      return;
    cancellation.Cancel();
    try
    {
      AutoSyncTask.Wait();
    }
    catch (AggregateException)
    {
    }
    cancellation.Dispose();
    AutoSyncCancellation = null;
    AutoSyncTask = null;
  }

  public bool IsReadOnly
  {
    get => Storage.IsReadOnly;
    set => Storage.IsReadOnly = value;
  }

  public ILogger Logger => Storage.Logger;

  public ITransactionalZoneTreeMaintenance<TKey, TValue> Maintenance => this;

  public IZoneTree<VersionedKey<TKey>, VersionedValue<TValue>> ZoneTree => Storage;

  public int ActiveTransactionCount
  {
    get
    {
      lock (StateLock)
        return activeTransactionCount;
    }
  }

  public long LastCommitTimestamp => Volatile.Read(ref lastCommitTimestamp);

  public int AbortedTransactionCount =>
      Entries.Values.Count(x => x.State == TransactionState.Aborted);

  public int ConflictIndexCount => ConflictIndex.Count;

  public IMaintainer CreateMaintainer() => Storage.CreateMaintainer();

  public void SaveMetaData() => StorageMaintenance.SaveMetaData();

  public void Drop()
  {
    DetachAndStop();
    StorageMaintenance.Drop();
  }

  void DetachAndStop()
  {
    lock (AutoSyncLock)
    {
      isDisposed = true;
      StopAutoSync();
    }
    StorageMaintenance.OnDiskSegmentCreated -= OnDiskSegmentCreated;
    StorageMaintenance.OnMergeOperationEnded -= OnMergeOperationEnded;
    StorageMaintenance.OnBottomSegmentsMergeOperationEnded -= OnBottomSegmentsMergeOperationEnded;
  }

  public void Dispose()
  {
    DetachAndStop();
    Storage.Dispose();
  }
}
