using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ZoneTree.Backup;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Transactional.Conflicts;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Transactional.Distributed;

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
/// committed. Recovery treats remaining status records as aborted transactions.
/// CommitDurableAsync keeps the transaction invisible until that deletion is synced.
/// A failed commit sync leaves its outcome in doubt and prevents further commits
/// until the tree is reopened; recovery decides whether its commit record survived.
/// Completed writes in synchronous WAL modes survive a process crash;
/// AsyncCompressed and AsyncShared can lose records still queued in process memory.
/// To wait for power-loss or operating-system-crash durability, await SyncWALAsync
/// after an ordinary commit, or use CommitDurableAsync to sync before publication.
/// Built-in transactional factories select Shared or AsyncShared WALs, which
/// recover one prefix of commits across segments, including every synced commit;
/// incomplete transactions remain invisible. Custom providers supply their own
/// recovery guarantees. None disables WAL durability.
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
public sealed class DistributedMvccZoneTree<TKey, TValue> :
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

  readonly ConcurrentDictionary<long, DistributedTransactionEntry> Entries = new();

  readonly Lock StateLock = new();

  readonly Lock CommitLock = new();

  readonly Queue<PendingCommit> PendingCommits = new();

  PendingCommit LastPendingCommit;

  bool CommitSyncRunning;

  Task CommitSyncTask;

  long LastPreparedCommitTimestamp;

  Exception CommitFailure;

  sealed class PendingCommit(DistributedMvccTransaction<TKey, TValue> transaction, bool ready)
  {
    internal readonly DistributedMvccTransaction<TKey, TValue> Transaction = transaction;
    internal readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Ready = ready;
    internal bool Synced;
  }

  readonly Lock AutoSyncLock = new();

  // The fields below are guarded by StateLock.

  long Clock;

  long ReservedClock;

  long lastCommitTimestamp;

  readonly DistributedActiveTransactions<TKey, TValue> Active = new();

  /// <summary>
  /// Committed entries, in commit order, until every snapshot sees them.
  /// </summary>
  readonly Queue<DistributedTransactionEntry> CommittedTransactions = new();

  /// <summary>
  /// Rolled back transactions whose versions were deleted, in rollback order.
  /// They are forgotten once no transaction that began before the deletions is active.
  /// </summary>
  readonly Queue<DistributedTransactionEntry> RolledBackTransactions = new();

  /// <summary>
  /// Committed entries, in commit order, whose commit record may not be durable yet.
  /// </summary>
  readonly Queue<DistributedTransactionEntry> NonDurableCommits = new();

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

  DistributedMvccZoneTree(
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
  internal static DistributedMvccZoneTree<TKey, TValue> Load(
      ZoneTreeOptions<TKey, TValue> options,
      bool canCreate,
      bool canOpen)
  {
    RejectUnsupportedOptions(options);
    var owner = new StrongBox<DistributedMvccZoneTree<TKey, TValue>>();
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
      owner.Value = new DistributedMvccZoneTree<TKey, TValue>(storage, options);
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
      StrongBox<DistributedMvccZoneTree<TKey, TValue>> owner)
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
    using var iterator = Storage.CreateIterator(IteratorType.NoRefresh, false, true);
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
      Entries[transactionId] = new DistributedTransactionEntry(transactionId, TransactionState.Aborted);
    }
    Clock = ReservedClock;
    lastCommitTimestamp = Clock;
    LastPreparedCommitTimestamp = Clock;
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
    DistributedMvccTransaction<TKey, TValue> transaction;
    lock (StateLock)
    {
      var snapshotTimestamp = lastCommitTimestamp;
      var id = NextTimestamp();
      transaction = new DistributedMvccTransaction<TKey, TValue>(
          this,
          Comparer,
          new DistributedTransactionEntry(id, TransactionState.Active),
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
    return new DistributedMvccReadTransaction<TKey, TValue>(this, Comparer, bucket);
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
  internal bool IsVisible(long version, DistributedMvccReader<TKey, TValue> transaction)
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
  internal DistributedVersionState GetVersionState(
      long version,
      long horizon,
      long durableCommitTimestamp,
      long mergedSegmentId,
      out long commitTimestamp)
  {
    commitTimestamp = 0;
    if (!Entries.TryGetValue(version, out var entry))
      return DistributedVersionState.Unregistered;
    switch (entry.State)
    {
      case TransactionState.Aborted:
        return DistributedVersionState.Aborted;
      case TransactionState.Committed:
        commitTimestamp = entry.CommitTimestamp;
        if (commitTimestamp > durableCommitTimestamp &&
            entry.CommitSegmentId > mergedSegmentId)
          return DistributedVersionState.Active;
        return commitTimestamp <= horizon ?
            DistributedVersionState.VisibleToAll :
            DistributedVersionState.Committed;
      default:
        return DistributedVersionState.Active;
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

  internal void OnFirstWrite(DistributedMvccTransaction<TKey, TValue> transaction)
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
  /// Validation and WAL append run under CommitLock. Durable commits publish
  /// only after syncing, without holding that lock during disk I/O. Later commits
  /// wait for the preceding publication so snapshots never acquire missing commits.
  /// </remarks>
  internal bool Commit(DistributedMvccTransaction<TKey, TValue> transaction)
      => PrepareCommit(transaction, false).GetAwaiter().GetResult();

  internal Task<bool> CommitDurableAsync(DistributedMvccTransaction<TKey, TValue> transaction)
      => PrepareCommit(transaction, true);

  Task<bool> PrepareCommit(DistributedMvccTransaction<TKey, TValue> transaction, bool durable)
  {
    var entry = transaction.Entry;
    if (!transaction.HasWrites)
    {
      // Nothing to publish: the transaction is equivalent to running at its snapshot.
      transaction.OnCommitted(transaction.SnapshotTimestamp);
      Entries.TryRemove(entry.Id, out _);
      EndTransaction(transaction);
      return durable ? SyncReadOnlyCommitAsync() : Task.FromResult(true);
    }

    var hasConflict = false;
    long commitTimestamp = 0;
    PendingCommit pending = null;
    lock (CommitLock)
    {
      try
      {
        if (CommitFailure != null)
          throw new InvalidOperationException("A previous commit sync failed. Reopen the tree before committing again.", CommitFailure);
        // Prepared commits reserve their keys too, even while invisible during sync.
        hasConflict =
            LastPreparedCommitTimestamp > transaction.SnapshotTimestamp &&
            transaction.ConflictsWith(ConflictIndex);
        if (!hasConflict)
        {
          var keys = transaction.GetWrittenKeys();
          lock (StateLock)
            commitTimestamp = NextTimestamp();
          // Committers check conflicts under this lock, so none sees the keys before the
          // commit point. Writers that see them early conflict with a commit that follows.
          // The keys added for a failed commit can only make transactions that began
          // before it conflict. They are pruned like the keys of a commit.
          ConflictIndex.Add(keys, commitTimestamp);
          LastPreparedCommitTimestamp = commitTimestamp;
          if (durable || PendingCommits.Count != 0)
            pending = new PendingCommit(transaction, !durable);
          // The commit point: a single record, durable with the next WAL sync.
          Storage.ForceDelete(VersionedKey<TKey>.TransactionStatus(transaction.TransactionId));
        }
      }
      catch
      {
        Rollback(transaction);
        throw;
      }
      if (!hasConflict)
      {
        entry.CommitTimestamp = commitTimestamp;
        // Read after the commit record was written, so it is at or after its segment.
        entry.CommitSegmentId = StorageMaintenance.MutableSegment.SegmentId;
        if (pending == null)
          PublishCommit(transaction, false);
        else
        {
          transaction.OnCommitting();
          entry.State = TransactionState.Committing;
          PendingCommits.Enqueue(pending);
          LastPendingCommit = pending;
          if (!CommitSyncRunning)
          {
            CommitSyncRunning = true;
            CommitSyncTask = Task.Run(SyncAndPublishAsync);
          }
        }
      }
    }
    if (hasConflict)
    {
      Rollback(transaction);
      return Task.FromResult(false);
    }
    if (pending == null)
      return Task.FromResult(true);
    return pending.Completion.Task;
  }

  async Task<bool> SyncReadOnlyCommitAsync()
  {
    await SyncWALAsync().ConfigureAwait(false);
    return true;
  }

  // Called under CommitLock. No disk I/O occurs during publication.
  void PublishCommit(DistributedMvccTransaction<TKey, TValue> transaction, bool synced)
  {
    var entry = transaction.Entry;
    entry.State = TransactionState.Committed;
    lock (StateLock)
    {
      Volatile.Write(ref lastCommitTimestamp, entry.CommitTimestamp);
      CommittedTransactions.Enqueue(entry);
      NonDurableCommits.Enqueue(entry);
      transaction.OnCommitted(entry.CommitTimestamp);
      if (synced && HasDurableWriteAheadLog)
        MarkCommitsDurable(entry.CommitTimestamp, 0);
      EndTransactionLocked(transaction);
    }
  }

  async Task SyncAndPublishAsync()
  {
    try
    {
      while (true)
      {
        PendingCommit covered;
        lock (CommitLock)
        {
          if (PendingCommits.Count == 0)
          {
            LastPendingCommit = null;
            CommitSyncRunning = false;
            return;
          }
          covered = LastPendingCommit;
        }
        // One worker batches all prepared records preceding this sync. The
        // commit lock is free during I/O, and cancelling a caller cannot stop it.
        await Storage.SyncWALAsync().ConfigureAwait(false);
        lock (CommitLock)
        {
          foreach (var item in PendingCommits)
          {
            item.Ready = item.Synced = true;
            if (item == covered)
              break;
          }
          while (PendingCommits.TryPeek(out var next) && next.Ready)
          {
            PublishCommit(next.Transaction, next.Synced);
            PendingCommits.Dequeue();
            next.Completion.TrySetResult(true);
          }
        }
      }
    }
    catch (Exception error)
    {
      lock (CommitLock)
      {
        CommitFailure ??= error;
        while (PendingCommits.TryDequeue(out var next))
        {
          // The commit record may have reached storage. Do not roll it back or
          // publish it after a failed sync; recovery determines its final outcome.
          if (next.Transaction.State != TransactionState.Committed)
          {
            next.Transaction.OnCommitInDoubt();
            next.Transaction.Entry.State = TransactionState.InDoubt;
          }
          if (next.Transaction.IsRegistered)
            EndTransaction(next.Transaction);
          next.Completion.TrySetException(error);
        }
        LastPendingCommit = null;
        CommitSyncRunning = false;
      }
    }
  }

  internal void Rollback(DistributedMvccTransaction<TKey, TValue> transaction)
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
        // Delete the status after its versions. An explicit WAL sync is needed
        // to guarantee that all of these deletions survive a crash together.
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

  void EndTransaction(DistributedMvccTransaction<TKey, TValue> transaction)
  {
    lock (StateLock)
      EndTransactionLocked(transaction);
  }

  /// <summary>
  /// Releases the snapshot of the transaction. Called under the state lock.
  /// </summary>
  void EndTransactionLocked(DistributedMvccTransaction<TKey, TValue> transaction)
  {
    Active.RemoveWriter(transaction);
    PruneCommittedTransactions();
    ForgetRolledBackTransactions();
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
    // Read-only transactions never check conflicts, so a long report
    // does not keep the committed keys.
    ConflictIndex.Prune(Active.OldestWriterSnapshot(lastCommitTimestamp));
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
    return new DistributedVersionGarbageFilter<TKey, TValue>(
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
      if (value != TimeSpan.Zero &&
          (value.TotalMilliseconds < 1 || value.TotalMilliseconds >= uint.MaxValue))
        throw new ArgumentOutOfRangeException(
            nameof(value), value,
            "The interval must be zero or at least 1 millisecond and less than 4294967295 milliseconds.");
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

  internal ILiveBackup CreateLiveBackup(LiveBackupOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    if (!options.IncludeInMemoryRecords)
      throw new ArgumentException("Transactional backups require in-memory records and transaction statuses.", nameof(options));
    return new LiveBackup<VersionedKey<TKey>, VersionedValue<TValue>>(Storage, options, CaptureLiveBackup);
  }

  ZoneTree<VersionedKey<TKey>, VersionedValue<TValue>>.BackupSegmentCollection CaptureLiveBackup()
  {
    lock (CommitLock)
    {
      // Keep the latest published snapshot visible to concurrent merge filters
      // until its immutable segment set is attached. Do not hold StateLock while
      // taking storage locks: merge filters also acquire StateLock.
      using var snapshot = BeginReadOnlyTransaction();
      // A prepared durable commit has already deleted its pending marker, but
      // is still invisible. Restore that marker in the backup's memory overlay.
      KeyValuePair<VersionedKey<TKey>, VersionedValue<TValue>>[] pending;
      lock (StateLock)
        pending = Entries.Values.Where(entry => entry.State != TransactionState.Committed)
            .Select(entry => new KeyValuePair<VersionedKey<TKey>, VersionedValue<TValue>>(
                VersionedKey<TKey>.TransactionStatus(entry.Id), VersionedValue<TValue>.FromSystem(PendingStatus)))
            .ToArray();
      var core = (ZoneTree<VersionedKey<TKey>, VersionedValue<TValue>>)Storage;
      var segments = core.CollectBackupSegments(moveMutableSegmentForward: true, includeInMemorySegments: true);
      segments.TreeKind = LiveBackupTreeKind.Transactional;
      segments.AdditionalRecords = pending;
      return segments;
    }
  }

  internal static async ValueTask<ITransactionalZoneTree<TKey, TValue>> RestoreLiveBackup(
      ZoneTreeOptions<TKey, TValue> options, ILiveBackupSource source, long? generationId)
  {
    RejectUnsupportedOptions(options);
    var storageOptions = CreateStorageOptions(options, new StrongBox<DistributedMvccZoneTree<TKey, TValue>>());
    var restore = new LiveBackupRestore<VersionedKey<TKey>, VersionedValue<TValue>>(
        storageOptions, source, LiveBackupTreeKind.Transactional);
    if (generationId.HasValue) await restore.RestoreGeneration(generationId.Value).ConfigureAwait(false);
    else await restore.RestoreLatest().ConfigureAwait(false);
    return Load(options, canCreate: false, canOpen: true);
  }

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
    Task commitSync;
    lock (CommitLock)
      commitSync = CommitSyncTask;
    commitSync?.GetAwaiter().GetResult();
    StorageMaintenance.OnMergeOperationEnded -= OnMergeOperationEnded;
  }

  public void Dispose()
  {
    DetachAndStop();
    Storage.Dispose();
  }
}
