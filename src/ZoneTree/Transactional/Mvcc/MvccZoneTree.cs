using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Transactional.Conflicts;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Transactional.Mvcc;

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
/// A rollback deletes its versions and then its status record in the storage tree.
/// The tree forgets the rollback once no transaction that began before the deletions
/// is active: the deletions hide the versions from readers, and merges drop the
/// versions where they meet the deletions.
/// </para>
/// <para>
/// Two committed transactions that wrote the same key never overlap, because the later
/// one would conflict. So among committed versions of a key, a higher transaction id is
/// a later commit, and the newest version a snapshot can see is its value.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class MvccZoneTree<TKey, TValue> :
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

  /// <summary>
  /// The user key comparer for the collections of the transactions.
  /// </summary>
  internal IComparer<TKey> KeyComparer { get; }

  readonly ConflictIndex<TKey> ConflictIndex;

  readonly ConcurrentDictionary<long, TransactionEntry> Entries = new();

  readonly Lock StateLock = new();

  readonly Lock CommitLock = new();

  readonly Lock AutoSyncLock = new();

  // The fields below are guarded by StateLock.

  long Clock;

  long ReservedClock;

  long lastCommitTimestamp;

  readonly ActiveTransactions<TKey, TValue> Active = new();

  /// <summary>
  /// Committed entries, in commit order, until every snapshot sees them.
  /// </summary>
  readonly Queue<TransactionEntry> CommittedTransactions = new();

  /// <summary>
  /// Committed keys, in commit order, until no writing transaction can conflict with them.
  /// </summary>
  readonly Queue<(long CommitTimestamp, TKey[] Keys)> CommittedWrites = new();

  /// <summary>
  /// Rolled back transactions whose versions were deleted, in rollback order.
  /// They are forgotten once no transaction that began before the deletions is active.
  /// </summary>
  readonly Queue<TransactionEntry> RolledBackTransactions = new();

  /// <summary>
  /// Committed entries, in commit order, whose commit record may not be durable yet.
  /// </summary>
  readonly Queue<TransactionEntry> NonDurableCommits = new();

  /// <summary>
  /// Every commit at or before it survives a crash. A merge may drop the versions a
  /// commit hides only when the commit is durable, otherwise a crash could keep the
  /// merge but lose the commit, and with it the only remaining value.
  /// </summary>
  long DurableCommitTimestamp;

  /// <summary>
  /// False when the write ahead log is disabled, so syncs make nothing durable.
  /// </summary>
  readonly bool HasDurableWriteAheadLog;

  TimeSpan autoSyncInterval;

  CancellationTokenSource AutoSyncCancellation;

  Task AutoSyncTask;

  bool isDisposed;

  internal IZoneTree<VersionedKey<TKey>, VersionedValue<TValue>> Storage { get; }

  IZoneTreeMaintenance<VersionedKey<TKey>, VersionedValue<TValue>> StorageMaintenance { get; }

  MvccZoneTree(
      IZoneTree<VersionedKey<TKey>, VersionedValue<TValue>> storage,
      ZoneTreeOptions<TKey, TValue> options)
  {
    Storage = storage;
    StorageMaintenance = storage.Maintenance;
    Comparer = options.Comparer;
    KeyComparer = new ComparerFromRefComparer<TKey>(Comparer);
    ConflictIndex = new ConflictIndex<TKey>(Comparer);
    HasDurableWriteAheadLog =
        options.WriteAheadLogOptions.WriteAheadLogMode != WriteAheadLogMode.None;
    Recover();
    ValidateSchema(options);
    StorageMaintenance.OnMergeOperationEnded += OnMergeOperationEnded;
  }

  /// <summary>
  /// Opens or creates a transactional tree with the given user options.
  /// </summary>
  internal static MvccZoneTree<TKey, TValue> Load(
      ZoneTreeOptions<TKey, TValue> options,
      bool canCreate,
      bool canOpen)
  {
    RejectUnsupportedOptions(options);
    var owner = new StrongBox<MvccZoneTree<TKey, TValue>>();
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
      owner.Value = new MvccZoneTree<TKey, TValue>(storage, options);
      return owner.Value;
    }
    catch
    {
      storage.Dispose();
      throw;
    }
  }

  /// <summary>
  /// The storage tree holds versions, not user records, so the options that act on
  /// user records cannot apply. They are rejected rather than silently ignored.
  /// </summary>
  static void RejectUnsupportedOptions(ZoneTreeOptions<TKey, TValue> options)
  {
    if (options.MergeFilterFactory != null)
      throw new InvalidOptionValueException(
          nameof(options.MergeFilterFactory),
          options.MergeFilterFactory,
          "A transactional tree filters its merges itself");
    if (options.IsRangeDeleted != null)
      throw new InvalidOptionValueException(
          nameof(options.IsRangeDeleted),
          options.IsRangeDeleted,
          "A transactional tree deletes keys only with transactions");
  }

  static ZoneTreeOptions<VersionedKey<TKey>, VersionedValue<TValue>> CreateStorageOptions(
      ZoneTreeOptions<TKey, TValue> options,
      StrongBox<MvccZoneTree<TKey, TValue>> owner)
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
      MergeFilterFactory = context => owner.Value?.CreateMergeFilter(context),
    };
  }

  /// <summary>
  /// The schema records share the clock record kind. Version 0 is the clock reservation.
  /// </summary>
  const long ComparerSchemaVersion = 1;

  const long KeySerializerSchemaVersion = 2;

  const long ValueSerializerSchemaVersion = 3;

  /// <summary>
  /// The stored type hashes of the user comparer and serializers, indexed by schema version.
  /// </summary>
  readonly long?[] StoredSchema = new long?[ValueSerializerSchemaVersion + 1];

  /// <summary>
  /// Restores the clock, the schema and the transactions that never committed
  /// from the system records.
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
        if (key.Version == 0)
          ReservedClock = iterator.CurrentValue.Meta;
        else if (key.Version < StoredSchema.Length)
          StoredSchema[key.Version] = iterator.CurrentValue.Meta;
        continue;
      }
      // The transaction did not commit before the tree closed, and its versions
      // were not all deleted. They may be anywhere in the tree, so it stays
      // registered as rolled back.
      var transactionId = key.Version;
      Entries[transactionId] = new TransactionEntry(transactionId, TransactionState.Aborted);
    }
    Clock = ReservedClock;
    lastCommitTimestamp = Clock;
    DurableCommitTimestamp = Clock;
  }

  /// <summary>
  /// The storage metadata records the wrapper types only, so the user comparer and
  /// serializer types are checked here. Reading with another encoding or order would
  /// silently return wrong data.
  /// </summary>
  void ValidateSchema(ZoneTreeOptions<TKey, TValue> options)
  {
    ValidateSchemaType(ComparerSchemaVersion, "comparer", options.Comparer);
    ValidateSchemaType(KeySerializerSchemaVersion, "key serializer", options.KeySerializer);
    ValidateSchemaType(ValueSerializerSchemaVersion, "value serializer", options.ValueSerializer);
  }

  void ValidateSchemaType(long version, string component, object instance)
  {
    var typeName = instance.GetType().SimplifiedFullName();
    var hash = HashTypeName(typeName);
    var stored = StoredSchema[version];
    if (stored == null)
    {
      Storage.Upsert(
          new VersionedKey<TKey>(VersionedKeyKind.Clock, default, version),
          VersionedValue<TValue>.FromSystem(hash));
      return;
    }
    if (stored.Value != hash)
      throw new TransactionalTreeSchemaMismatchException(component, typeName);
  }

  /// <summary>
  /// 64-bit FNV-1a of the type name. Stable across processes and platforms.
  /// </summary>
  static long HashTypeName(string typeName)
  {
    var hash = 14695981039346656037UL;
    foreach (var c in typeName)
    {
      hash = (hash ^ c) * 1099511628211UL;
    }
    return unchecked((long)hash);
  }

  public ITransaction<TKey, TValue> BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable)
  {
    if (!Enum.IsDefined(isolationLevel))
      throw new ArgumentOutOfRangeException(nameof(isolationLevel), isolationLevel, null);
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    MvccTransaction<TKey, TValue> transaction;
    lock (StateLock)
    {
      var snapshotTimestamp = lastCommitTimestamp;
      var id = NextTimestamp();
      transaction = new MvccTransaction<TKey, TValue>(
          this,
          Comparer,
          new TransactionEntry(id, TransactionState.Active),
          snapshotTimestamp,
          isolationLevel);
      Active.AddWriter(transaction);
    }
    // Readers look up the entry only for versions of this transaction,
    // and it writes none before this returns.
    Entries[transaction.TransactionId] = transaction.Entry;
    return transaction;
  }

  public IReadTransaction<TKey, TValue> BeginReadOnlyTransaction()
  {
    // Join the bucket of the latest snapshot without the state lock. A bucket of an
    // older snapshot is never joined, so a reader sees every commit that finished
    // before it began, including the caller's own.
    var bucket = Active.CurrentBucket;
    if (bucket == null ||
        bucket.SnapshotTimestamp != Volatile.Read(ref lastCommitTimestamp) ||
        !bucket.TryJoin())
    {
      lock (StateLock)
      {
        bucket = Active.CurrentBucket;
        if (bucket == null ||
            bucket.SnapshotTimestamp != lastCommitTimestamp ||
            !bucket.TryJoin())
          bucket = Active.AddBucket(lastCommitTimestamp, Clock);
      }
    }
    return new MvccReadTransaction<TKey, TValue>(this, Comparer, bucket);
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

  /// <summary>
  /// The oldest snapshot timestamp in use. Every commit at or before it
  /// is visible to every active and future snapshot. Called under the state lock.
  /// </summary>
  long GetHorizon() => Active.OldestSnapshot(lastCommitTimestamp);

  /// <summary>
  /// Checks whether a version is in the snapshot of a transaction.
  /// </summary>
  internal bool IsVisible(long version, MvccReader<TKey, TValue> transaction)
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

  /// <summary>
  /// Classifies a version for a merge. A commit that may not survive a crash
  /// counts as active: the merge keeps its version and drops nothing under it.
  /// </summary>
  /// <param name="version">The version.</param>
  /// <param name="horizon">The oldest snapshot.</param>
  /// <param name="durableCommitTimestamp">Commits at or before it are durable.</param>
  /// <param name="mergedSegmentId">Commit records in this segment or an older one
  /// become durable with the merge output.</param>
  /// <param name="commitTimestamp">The commit timestamp of a committed version.</param>
  internal VersionState GetVersionState(
      long version,
      long horizon,
      long durableCommitTimestamp,
      long mergedSegmentId,
      out long commitTimestamp)
  {
    commitTimestamp = 0;
    if (!Entries.TryGetValue(version, out var entry))
      return VersionState.Unregistered;
    switch (entry.State)
    {
      case TransactionState.Aborted:
        return VersionState.Aborted;
      case TransactionState.Committed:
        commitTimestamp = entry.CommitTimestamp;
        if (commitTimestamp > durableCommitTimestamp &&
            entry.CommitSegmentId > mergedSegmentId)
          return VersionState.Active;
        return commitTimestamp <= horizon ?
            VersionState.VisibleToAll :
            VersionState.Committed;
      default:
        return VersionState.Active;
    }
  }

  /// <summary>
  /// Marks the commits made durable by a write ahead log sync or a merge.
  /// Called under the state lock.
  /// </summary>
  void MarkCommitsDurable(long syncedCommitTimestamp, long mergedSegmentId)
  {
    while (NonDurableCommits.TryPeek(out var entry) &&
        (entry.CommitTimestamp <= syncedCommitTimestamp ||
         entry.CommitSegmentId <= mergedSegmentId))
    {
      NonDurableCommits.Dequeue();
      DurableCommitTimestamp = entry.CommitTimestamp;
    }
    PruneCommittedTransactions();
  }

  internal bool HasCommitAfter(in TKey key, long timestamp)
      => ConflictIndex.HasCommitAfter(in key, timestamp);

  internal void OnFirstWrite(MvccTransaction<TKey, TValue> transaction)
  {
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
  /// <remarks>
  /// Every step that calls the key comparer or can fail runs before the commit point,
  /// and a failure there rolls the transaction back. After the commit point only plain
  /// state changes publish the commit, so a commit is never reported as rolled back.
  /// </remarks>
  internal bool Commit(MvccTransaction<TKey, TValue> transaction)
  {
    var entry = transaction.Entry;
    if (!transaction.HasWrites)
    {
      // Nothing to publish: the transaction is equivalent to running at its snapshot.
      transaction.OnCommitted(transaction.SnapshotTimestamp);
      Entries.TryRemove(entry.Id, out _);
      EndTransaction(transaction);
      return true;
    }

    var hasConflict = false;
    long commitTimestamp = 0;
    TKey[] keys = null;
    lock (CommitLock)
    {
      try
      {
        // Commits only happen under this lock, so no commit since the snapshot means no conflict.
        hasConflict =
            Volatile.Read(ref lastCommitTimestamp) > transaction.SnapshotTimestamp &&
            transaction.ConflictsWith(ConflictIndex);
        if (!hasConflict)
        {
          keys = transaction.GetWrittenKeys();
          lock (StateLock)
            commitTimestamp = NextTimestamp();
          // Committers check conflicts under this lock, so none sees the keys before the
          // commit point. Writers that see them early conflict with a commit that follows.
          ConflictIndex.Add(keys, commitTimestamp);
          // The commit point: a single record, durable with the next WAL sync.
          Storage.ForceDelete(VersionedKey<TKey>.TransactionStatus(transaction.TransactionId));
        }
      }
      catch
      {
        if (commitTimestamp != 0)
        {
          // The keys added for a failed commit can only make transactions that began
          // before it conflict. They are pruned like the keys of a commit.
          lock (StateLock)
            CommittedWrites.Enqueue((commitTimestamp, keys));
        }
        Rollback(transaction);
        throw;
      }
      if (!hasConflict)
      {
        transaction.OnCommitted(commitTimestamp);
        entry.CommitTimestamp = commitTimestamp;
        // Read after the commit record was written, so it is at or after its segment.
        entry.CommitSegmentId = StorageMaintenance.MutableSegment.SegmentId;
        entry.State = TransactionState.Committed;
        lock (StateLock)
        {
          // New snapshots include the commit only after its entry says committed.
          Volatile.Write(ref lastCommitTimestamp, commitTimestamp);
          CommittedTransactions.Enqueue(entry);
          CommittedWrites.Enqueue((commitTimestamp, keys));
          NonDurableCommits.Enqueue(entry);
        }
      }
    }
    if (hasConflict)
    {
      Rollback(transaction);
      return false;
    }
    EndTransaction(transaction);
    return true;
  }

  internal void Rollback(MvccTransaction<TKey, TValue> transaction)
  {
    var entry = transaction.Entry;
    var isDeleted = false;
    // Readers and merges ignore the versions from now on.
    entry.State = TransactionState.Aborted;
    if (!transaction.HasWrites)
    {
      Entries.TryRemove(entry.Id, out _);
    }
    else
    {
      try
      {
        foreach (var key in transaction.WrittenKeysForRollback)
          Storage.ForceDelete(VersionedKey<TKey>.Data(in key, transaction.TransactionId));
        // The storage tree keeps the write order across a crash, so a recovered
        // tree keeps the pending status record unless it keeps every deletion.
        Storage.ForceDelete(VersionedKey<TKey>.TransactionStatus(transaction.TransactionId));
        isDeleted = true;
      }
      catch (Exception e)
      {
        // Versions may lack their deletions, so the entry stays for good.
        Logger.LogError(e);
      }
    }
    lock (StateLock)
    {
      if (isDeleted)
      {
        entry.AbortSequence = Clock;
        RolledBackTransactions.Enqueue(entry);
      }
      EndTransactionLocked(transaction);
    }
  }

  void EndTransaction(MvccTransaction<TKey, TValue> transaction)
  {
    lock (StateLock)
      EndTransactionLocked(transaction);
  }

  /// <summary>
  /// Releases the snapshot of the transaction. Called under the state lock.
  /// </summary>
  void EndTransactionLocked(MvccTransaction<TKey, TValue> transaction)
  {
    Active.RemoveWriter(transaction);
    // The transaction ended whatever follows. The bookkeeping calls the key
    // comparer, and a failure leaves its work to the next call.
    try
    {
      PruneCommittedTransactions();
      ForgetRolledBackTransactions();
    }
    catch (Exception e)
    {
      Logger.LogError(e);
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
      // A missing entry means durable and visible to all, which lets merges
      // drop the versions it hides.
      var horizon = Math.Min(GetHorizon(), DurableCommitTimestamp);
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
      var writerHorizon = Active.OldestWriterSnapshot(lastCommitTimestamp);
      while (CommittedWrites.TryPeek(out var item) &&
          item.CommitTimestamp <= writerHorizon)
      {
        // Removed before the item, so a failure leaves the keys to a later call.
        ConflictIndex.Remove(item.Keys, item.CommitTimestamp);
        CommittedWrites.Dequeue();
      }
    }
  }

  /// <summary>
  /// Forgets the rolled back transactions whose deletions every active
  /// transaction reads. Called under the state lock.
  /// </summary>
  /// <remarks>
  /// From then on the deletions hide the versions from readers, and merges
  /// drop the versions where they meet the deletions.
  /// </remarks>
  void ForgetRolledBackTransactions()
  {
    if (RolledBackTransactions.Count == 0)
      return;
    // A transaction that began before the deletions may hold an iterator
    // over segments that have the versions without the deletions.
    var oldestBeginSequence = Active.OldestBeginSequence(long.MaxValue);
    while (RolledBackTransactions.TryPeek(out var entry) &&
        entry.AbortSequence < oldestBeginSequence)
    {
      RolledBackTransactions.Dequeue();
      Entries.TryRemove(entry.Id, out _);
    }
  }

  /// <summary>
  /// Checks whether the newest stored copy of a version is a deletion. The versions
  /// of a forgotten rollback have no entry, like the committed ones, and only their
  /// deletions tell them apart.
  /// </summary>
  internal bool IsDeletedVersion(in VersionedKey<TKey> key)
  {
    var probe = new DeletionProbe(Comparer, in key);
    Storage.VisitSegments(in key, ref probe);
    return probe.IsDeleted;
  }

  /// <summary>
  /// Finds the newest stored copy of a version. A deletion is newer than
  /// the version it deletes, and segments are visited newest first.
  /// </summary>
  struct DeletionProbe : ISegmentRecordVisitor<VersionedKey<TKey>, VersionedValue<TValue>>
  {
    readonly IRefComparer<TKey> Comparer;

    readonly VersionedKey<TKey> Key;

    public bool IsDeleted;

    public DeletionProbe(IRefComparer<TKey> comparer, in VersionedKey<TKey> key)
    {
      Comparer = comparer;
      Key = key;
    }

    public SegmentVisitAction Visit(in VersionedKey<TKey> key, in VersionedValue<TValue> value)
    {
      if (key.Kind != Key.Kind ||
          key.Version != Key.Version ||
          Comparer.Compare(in key.Key, in Key.Key) != 0)
        return SegmentVisitAction.NextSegment;
      IsDeleted = value.Kind == VersionedValueKind.Deleted;
      return SegmentVisitAction.Stop;
    }
  }

  internal IMergeFilter<VersionedKey<TKey>, VersionedValue<TValue>> CreateMergeFilter(
      MergeFilterContext context)
  {
    long[] snapshots;
    long durableCommitTimestamp;
    lock (StateLock)
    {
      // Snapshots that begin during the merge read the latest commit or a later one.
      snapshots = Active.GetSnapshots(lastCommitTimestamp);
      durableCommitTimestamp = DurableCommitTimestamp;
    }
    return new VersionGarbageFilter<TKey, TValue>(
        this,
        Comparer,
        context.IncludesOldestRecords,
        snapshots,
        durableCommitTimestamp,
        context.NewestMergedSegmentId);
  }

  /// <summary>
  /// Commit records merged into the disk segment are durable with it. Read-only
  /// transactions end without the state lock, so every merge end also forgets
  /// what they kept.
  /// </summary>
  void OnMergeOperationEnded(
      IZoneTreeMaintenance<VersionedKey<TKey>, VersionedValue<TValue>> zoneTree,
      MergeResult mergeResult)
  {
    var mergedSegmentId = mergeResult == MergeResult.SUCCESS ? GetMergedSegmentId() : 0;
    lock (StateLock)
    {
      MarkCommitsDurable(0, mergedSegmentId);
      ForgetRolledBackTransactions();
    }
  }

  /// <summary>
  /// The newest mutable segment id whose records are all in disk segments:
  /// every segment older than the remaining in-memory segments was merged.
  /// </summary>
  long GetMergedSegmentId()
  {
    var oldest = StorageMaintenance.MutableSegment.SegmentId;
    foreach (var segment in StorageMaintenance.ReadOnlySegments)
      oldest = Math.Min(oldest, segment.SegmentId);
    return oldest - 1;
  }

  public async Task SyncWALAsync(CancellationToken cancellationToken = default)
  {
    // The sync covers every commit record written before the call.
    var syncedCommitTimestamp = LastCommitTimestamp;
    await Storage.SyncWALAsync(cancellationToken).ConfigureAwait(false);
    if (!HasDurableWriteAheadLog)
      return;
    lock (StateLock)
      MarkCommitsDurable(syncedCommitTimestamp, 0);
  }

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
            await SyncWALAsync(cancellation.Token).ConfigureAwait(false);
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
        return Active.Count;
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
    StorageMaintenance.OnMergeOperationEnded -= OnMergeOperationEnded;
  }

  public void Dispose()
  {
    DetachAndStop();
    Storage.Dispose();
  }
}
