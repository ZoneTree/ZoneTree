using ZoneTree.Transactional;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Cluster;

/// <summary>
/// The MVCC tree of a coordinated column: every version of a key in a single storage tree,
/// with snapshot reads and optimistic, commit-time conflict detection. The column's domain
/// coordinates its transactions with the other columns of the domain.
/// </summary>
/// <remarks>
/// <para>
/// The domain's clock issues transaction ids and commit timestamps, and a domain transaction
/// reads one snapshot in every column. Writes are stored at once under the transaction id, so
/// they persist and merge like any other record, and they stay invisible to others until the
/// commit.
/// </para>
/// <para>
/// A transaction that writes stores a pending status record before its first version. Its
/// commit replaces that record with the commit timestamp, in every column it wrote. The
/// commit becomes visible when the domain's snapshot timestamp reaches it, after it is
/// published in every column, so a reader sees all of its writes or none.
/// </para>
/// <para>
/// Recovery treats remaining pending records as rolled back. A commit record counts unless
/// its timestamp is in a range the domain rolled back after an unclean shutdown: then the
/// record becomes pending again. So every column keeps the same prefix of the domain's commits.
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
internal sealed class ClusterMvccZoneTree<TKey, TValue> :
    ITransactionalZoneTreeMaintenance<TKey, TValue>
{
  /// <summary>The status of a transaction that wrote and did not commit yet.</summary>
  const long PendingStatus = CommitRecord.Pending;

  /// <summary>
  /// The committed transactions that wait for the domain to make them durable. Beyond it,
  /// the tree asks for a domain sync, so their entries and the versions they hide do not
  /// pile up when nothing else syncs.
  /// </summary>
  const int UnsyncedCommitThreshold = 4096;

  readonly IRefComparer<TKey> Comparer;

  /// <summary>
  /// The user key comparer for the collections of the transactions.
  /// </summary>
  internal IComparer<TKey> KeyComparer { get; }

  readonly ConflictIndex<TKey> ConflictIndex;

  readonly ConcurrentDictionary<long, TransactionEntry> Entries = new();

  readonly Lock StateLock = new();

  readonly Lock CommitLock = new();

  readonly DomainCoordinator Coordinator;

  long LastPreparedCommitTimestamp;

  /// <summary>One if the tree wrote commit records since its last sync.</summary>
  int HasUnsyncedCommits;

  /// <summary>One while a sync the tree asked for is pending.</summary>
  int IsSyncRequested;

  /// <summary>The commits published since the domain last made commits durable. Guarded by StateLock.</summary>
  int UnsyncedCommitCount;

  // The fields below are guarded by StateLock.

  /// <summary>
  /// Committed entries, in commit order, until every snapshot sees them and they are durable.
  /// </summary>
  readonly Queue<TransactionEntry> CommittedTransactions = new();

  /// <summary>
  /// Rolled back transactions whose versions were deleted, in rollback order.
  /// They are forgotten once no transaction that began before the deletions is active.
  /// </summary>
  readonly Queue<TransactionEntry> RolledBackTransactions = new();

  internal IZoneTree<VersionedKey<TKey>, VersionedValue<TValue>> Storage { get; }

  IZoneTreeMaintenance<VersionedKey<TKey>, VersionedValue<TValue>> StorageMaintenance { get; }

  ClusterMvccZoneTree(
      IZoneTree<VersionedKey<TKey>, VersionedValue<TValue>> storage,
      ZoneTreeOptions<TKey, TValue> options,
      DomainCoordinator coordinator,
      Func<long, bool> isRolledBack)
  {
    Storage = storage;
    StorageMaintenance = storage.Maintenance;
    Comparer = options.Comparer;
    KeyComparer = new ComparerFromRefComparer<TKey>(Comparer);
    ConflictIndex = new ConflictIndex<TKey>(Comparer);
    Coordinator = coordinator;
    Recover(isRolledBack);
    ValidateSchema(options);
    StorageMaintenance.OnMergeOperationEnded += OnMergeOperationEnded;
  }

  /// <summary>
  /// Opens or creates the tree of a coordinated column.
  /// </summary>
  /// <param name="options">The user options of the column.</param>
  /// <param name="canCreate">True if the tree may be created.</param>
  /// <param name="canOpen">True if an existing tree may be opened.</param>
  /// <param name="coordinator">The coordinator of the column's domain.</param>
  /// <param name="isRolledBack">True for the commit timestamps the domain rolled back.</param>
  internal static ClusterMvccZoneTree<TKey, TValue> Load(
      ZoneTreeOptions<TKey, TValue> options,
      bool canCreate,
      bool canOpen,
      DomainCoordinator coordinator,
      Func<long, bool> isRolledBack)
  {
    RejectUnsupportedOptions(options);
    var owner = new StrongBox<ClusterMvccZoneTree<TKey, TValue>>();
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
      owner.Value = new ClusterMvccZoneTree<TKey, TValue>(storage, options, coordinator, isRolledBack);
      return owner.Value;
    }
    catch
    {
      storage.Dispose();
      throw;
    }
  }

  /// <summary>
  /// Restores a backup generation of the storage tree of a coordinated column, which the
  /// column opens afterward.
  /// </summary>
  internal static async ValueTask RestoreStorageAsync(ZoneTreeOptions<TKey, TValue> options,
      Backup.ILiveBackupSource source, long generationId)
  {
    RejectUnsupportedOptions(options);
    // Nothing merges during a restore, so the storage needs no tree for its merge filter.
    var storageOptions = CreateStorageOptions(options, new StrongBox<ClusterMvccZoneTree<TKey, TValue>>());
    var restore = new Backup.LiveBackupRestore<VersionedKey<TKey>, VersionedValue<TValue>>(
        storageOptions, source, Backup.LiveBackupTreeKind.Plain);
    await restore.RestoreGeneration(generationId).ConfigureAwait(false);
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
      StrongBox<ClusterMvccZoneTree<TKey, TValue>> owner)
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
  /// The schema records share the clock record kind, from version 1 on.
  /// </summary>
  const long ComparerSchemaVersion = 1;

  const long KeySerializerSchemaVersion = 2;

  const long ValueSerializerSchemaVersion = 3;

  /// <summary>
  /// The stored type hashes of the user comparer and serializers, indexed by schema version.
  /// </summary>
  readonly long?[] StoredSchema = new long?[ValueSerializerSchemaVersion + 1];

  /// <summary>
  /// Restores the schema and the transactions that never committed from the system records.
  /// </summary>
  /// <remarks>
  /// A pending record stays registered as rolled back: its transaction did not commit, and
  /// its versions may be anywhere in the tree. A commit record in a range the domain rolled
  /// back becomes pending again, so the rollback holds in every later recovery too.
  /// </remarks>
  void Recover(Func<long, bool> isRolledBack)
  {
    var rolledBack = new List<long>();
    using (var iterator = Storage.CreateIterator(IteratorType.NoRefresh, false, true))
    {
      iterator.SeekFirst();
      while (iterator.Next())
      {
        var key = iterator.CurrentKey;
        if (key.Kind == VersionedKeyKind.Data)
          break;
        var value = iterator.CurrentValue;
        if (key.Kind == VersionedKeyKind.Clock)
        {
          if (key.Version > 0 && key.Version < StoredSchema.Length)
            StoredSchema[key.Version] = value.Meta;
          continue;
        }
        var transactionId = key.Version;
        if (value.Meta == PendingStatus)
          Entries[transactionId] = new TransactionEntry(transactionId, TransactionState.Aborted);
        else if (isRolledBack(CommitRecord.Sequence(value.Meta)))
          rolledBack.Add(transactionId);
      }
    }
    foreach (var transactionId in rolledBack)
    {
      Storage.Upsert(
          VersionedKey<TKey>.TransactionStatus(transactionId),
          VersionedValue<TValue>.FromSystem(PendingStatus));
      Entries[transactionId] = new TransactionEntry(transactionId, TransactionState.Aborted);
    }
    LastPreparedCommitTimestamp = Coordinator.Sequencer.SnapshotTimestamp;
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

  /// <summary>
  /// Begins the view of a writing domain transaction, with the domain transaction's id
  /// and snapshot.
  /// </summary>
  internal ClusterMvccTransaction<TKey, TValue> BeginTransaction(
      TransactionIsolationLevel isolationLevel, long transactionId, long snapshotTimestamp)
  {
    if (!Enum.IsDefined(isolationLevel))
      throw new ArgumentOutOfRangeException(nameof(isolationLevel), isolationLevel, null);
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    var transaction = new ClusterMvccTransaction<TKey, TValue>(
        this,
        Comparer,
        new TransactionEntry(transactionId, TransactionState.Active),
        snapshotTimestamp,
        isolationLevel);
    // The domain keeps the view's snapshot and begin sequence. Readers look up the entry
    // only for versions of this transaction, and it writes none before this returns.
    Entries[transactionId] = transaction.Entry;
    return transaction;
  }

  /// <summary>
  /// Begins the view of a read-only domain transaction at the domain transaction's snapshot.
  /// </summary>
  internal ClusterMvccReadTransaction<TKey, TValue> BeginReadOnlyTransaction(long snapshotTimestamp)
      // The domain keeps the snapshot and the begin sequence of its read-only transactions.
      => new(this, Comparer, snapshotTimestamp);

  /// <summary>
  /// The oldest snapshot timestamp in use in the domain. Every commit at or before it
  /// is visible to every active and future snapshot.
  /// </summary>
  long GetHorizon() => Coordinator.Snapshots.OldestSnapshot;

  /// <summary>
  /// Checks whether a version is in the snapshot of a transaction.
  /// </summary>
  internal bool IsVisible(long version, ClusterMvccReader<TKey, TValue> transaction)
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
  /// Classifies a version for a merge. A commit that is not durable in the whole domain counts
  /// as active: recovery may roll it back, so the merge keeps its version and drops nothing
  /// under it.
  /// </summary>
  /// <param name="version">The version.</param>
  /// <param name="horizon">The oldest snapshot.</param>
  /// <param name="durableCommitTimestamp">Commits at or before it are durable in the domain.</param>
  /// <param name="commitTimestamp">The commit timestamp of a committed version.</param>
  internal VersionState GetVersionState(
      long version,
      long horizon,
      long durableCommitTimestamp,
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
        if (commitTimestamp > durableCommitTimestamp)
          return VersionState.Active;
        return commitTimestamp <= horizon ?
            VersionState.VisibleToAll :
            VersionState.Committed;
      default:
        return VersionState.Active;
    }
  }

  /// <summary>
  /// Checks whether a status record can leave the tree in a merge that includes the oldest
  /// records: a commit record whose entry is gone. An entry of this process goes only once
  /// its commit is durable in the domain and visible to every snapshot, and a commit that
  /// recovery kept has no entry. Without the record, its versions count as committed.
  /// </summary>
  internal bool CanDropStatus(long transactionId, in VersionedValue<TValue> value)
      => value.Kind == VersionedValueKind.System && value.Meta != PendingStatus &&
          !Entries.ContainsKey(transactionId);

  /// <summary>
  /// Called by the coordinator after the domain made more commits durable.
  /// </summary>
  internal void OnDomainDurable()
  {
    lock (StateLock)
    {
      UnsyncedCommitCount = 0;
      Volatile.Write(ref IsSyncRequested, 0);
      PruneCommittedTransactions();
    }
  }

  internal bool HasCommitAfter(in TKey key, long timestamp)
      => ConflictIndex.HasCommitAfter(in key, timestamp);

  internal void OnFirstWrite(ClusterMvccTransaction<TKey, TValue> transaction)
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
  /// Validates a view of a committing domain transaction against the commits after its
  /// snapshot: its writes, and in serializable mode its reads, even in a view that wrote
  /// nothing. Called under the column's commit gate, after the view's operations stopped.
  /// </summary>
  internal bool Validate(ClusterMvccTransaction<TKey, TValue> transaction)
  {
    lock (CommitLock)
    {
      Coordinator.ThrowIfCommitsStopped();
      if (transaction.HasConflict)
        return false;
      // Prepared commits reserve their keys too, even while invisible during sync.
      return LastPreparedCommitTimestamp <= transaction.SnapshotTimestamp ||
          !transaction.ConflictsWith(ConflictIndex);
    }
  }

  /// <summary>
  /// Prepares the commit of a validated view that wrote: registers its keys at the commit
  /// timestamp. Called under the column's commit gate, so it does no I/O.
  /// </summary>
  internal void Prepare(ClusterMvccTransaction<TKey, TValue> transaction, long commitTimestamp)
  {
    var entry = transaction.Entry;
    lock (CommitLock)
    {
      var keys = transaction.CommitKeys;
      // Committers check conflicts under the commit gate, so none sees the keys before the
      // commit. Writers that see them early conflict with a commit that follows. The keys
      // of a failed commit can only make transactions that began before it conflict, and
      // they are pruned like the keys of a commit.
      ConflictIndex.Add(keys, commitTimestamp);
      LastPreparedCommitTimestamp = commitTimestamp;
      entry.CommitTimestamp = commitTimestamp;
      entry.State = TransactionState.Committing;
      transaction.OnCommitting();
    }
  }

  /// <summary>
  /// Writes the commit record of a prepared view, after the commit gates are released: the
  /// write may rotate the mutable segment, which syncs the disk. The commit is visible once
  /// the domain's snapshot reaches it, and durable once the domain's durable timestamp
  /// does, which waits for this record.
  /// </summary>
  internal void WriteCommitRecord(ClusterMvccTransaction<TKey, TValue> transaction)
  {
    transaction.HasCommitRecord = true;
    try
    {
      Storage.Upsert(
          VersionedKey<TKey>.TransactionStatus(transaction.TransactionId),
          VersionedValue<TValue>.FromSystem(transaction.CommitMeta));
    }
    catch (ZoneTreeIsReadOnlyException)
    {
      // A read-only tree rejects the write before it stores anything.
      transaction.HasCommitRecord = false;
      throw;
    }
    Volatile.Write(ref HasUnsyncedCommits, 1);
  }

  /// <summary>
  /// Publishes a prepared view. Readers see it once the domain's snapshot reaches its
  /// commit timestamp, which happens after every view of the transaction is published.
  /// </summary>
  internal void Publish(ClusterMvccTransaction<TKey, TValue> transaction)
  {
    var entry = transaction.Entry;
    entry.State = TransactionState.Committed;
    bool requestSync;
    lock (StateLock)
    {
      CommittedTransactions.Enqueue(entry);
      transaction.OnCommitted(entry.CommitTimestamp);
      EndTransactionLocked(transaction);
      // Entries a long reader keeps do not count: a sync would not release them.
      requestSync = ++UnsyncedCommitCount >= UnsyncedCommitThreshold;
    }
    if (requestSync && Interlocked.Exchange(ref IsSyncRequested, 1) == 0)
      _ = Coordinator.SyncAsync();
  }

  /// <summary>
  /// Ends a view that wrote nothing in a committed domain transaction. It is equivalent to
  /// running at its snapshot.
  /// </summary>
  internal void EndUnwritten(ClusterMvccTransaction<TKey, TValue> transaction)
  {
    transaction.OnCommitted(transaction.SnapshotTimestamp);
    Entries.TryRemove(transaction.TransactionId, out _);
    EndTransaction(transaction);
  }

  /// <summary>
  /// Leaves a prepared view in doubt after the domain failed to make its commit durable.
  /// Recovery decides its outcome when the cluster reopens.
  /// </summary>
  internal void LeaveInDoubt(ClusterMvccTransaction<TKey, TValue> transaction)
  {
    transaction.OnCommitInDoubt();
    transaction.Entry.State = TransactionState.InDoubt;
    EndTransaction(transaction);
  }

  /// <summary>
  /// Starts a sync of the write ahead log if the tree wrote commit records since its last
  /// sync, or returns null. Called by the domain's sync thread, which starts the syncs of
  /// all its columns before it waits for them. The WAL completes a sync on the calling
  /// thread or on its writer thread, never on the thread pool.
  /// </summary>
  internal Task StartSync()
  {
    if (Interlocked.Exchange(ref HasUnsyncedCommits, 0) == 0)
      return null;
    try
    {
      return Storage.SyncWALAsync();
    }
    catch
    {
      OnSyncFailed();
      throw;
    }
  }

  /// <summary>Called after a sync failed, so the next domain sync syncs the tree again.</summary>
  internal void OnSyncFailed() => Volatile.Write(ref HasUnsyncedCommits, 1);

  /// <summary>
  /// Rolls back a view: deletes its versions and its status record. Returns false if a
  /// deletion failed; the entry then stays, so the versions remain invisible.
  /// </summary>
  internal bool Rollback(ClusterMvccTransaction<TKey, TValue> transaction)
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
    // The deletion of a commit record must be durable before the domain's durable timestamp
    // reaches the commit, so the next domain sync syncs this tree.
    if (transaction.HasCommitRecord)
      Volatile.Write(ref HasUnsyncedCommits, 1);
    lock (StateLock)
    {
      if (isDeleted)
      {
        entry.AbortSequence = Coordinator.Clock.Current;
        RolledBackTransactions.Enqueue(entry);
      }
      EndTransactionLocked(transaction);
    }
    return isDeleted || !transaction.HasWrites;
  }

  void EndTransaction(ClusterMvccTransaction<TKey, TValue> transaction)
  {
    lock (StateLock)
      EndTransactionLocked(transaction);
  }

  /// <summary>
  /// Prunes what the ended view kept. Called under the state lock.
  /// </summary>
  void EndTransactionLocked(ClusterMvccTransaction<TKey, TValue> transaction)
  {
    PruneCommittedTransactions();
    ForgetRolledBackTransactions();
  }

  /// <summary>
  /// Forgets the commits every snapshot sees and the domain made durable, and the committed
  /// keys no writing transaction can conflict with. Called under the state lock.
  /// </summary>
  void PruneCommittedTransactions()
  {
    if (CommittedTransactions.Count > 0)
    {
      // A missing entry means durable and visible to all, which lets merges
      // drop the versions it hides.
      var horizon = Math.Min(GetHorizon(), Coordinator.DurableTimestamp);
      while (CommittedTransactions.TryPeek(out var entry) &&
          entry.CommitTimestamp <= horizon)
      {
        CommittedTransactions.Dequeue();
        Entries.TryRemove(entry.Id, out _);
      }
    }
    // Read-only transactions never check conflicts, so a long report
    // does not keep the committed keys.
    ConflictIndex.Prune(Coordinator.Snapshots.OldestWriterSnapshot);
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
    // A transaction that began before the deletions may hold an iterator over segments
    // that have the versions without the deletions. The domain knows when its
    // transactions began, including those that have no view in this tree yet.
    var oldestBeginSequence = Coordinator.Snapshots.OldestBeginSequence;
    while (RolledBackTransactions.TryPeek(out var entry) &&
        entry.AbortSequence < oldestBeginSequence)
    {
      RolledBackTransactions.Dequeue();
      Entries.TryRemove(entry.Id, out _);
    }
  }

  /// <summary>
  /// The outcome of a transaction in this column, if the column knows it.
  /// </summary>
  internal TransactionOutcome GetOutcome(long transactionId)
  {
    if (Entries.TryGetValue(transactionId, out var entry))
    {
      return entry.State switch
      {
        TransactionState.Committed => TransactionOutcome.Committed,
        TransactionState.Aborted => TransactionOutcome.Aborted,
        _ => TransactionOutcome.Pending,
      };
    }
    if (!Storage.TryGet(VersionedKey<TKey>.TransactionStatus(transactionId), out var status) ||
        status.Kind != VersionedValueKind.System)
      return TransactionOutcome.Unknown;
    return status.Meta == PendingStatus ? TransactionOutcome.Aborted : TransactionOutcome.Committed;
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
    // A merge of in-memory segments takes their commit records out of the journal, which is
    // all that recovery reads. The column first makes every commit they can hold durable,
    // and records that in its journal, so recovery counts those commits as complete.
    if (context.NewestMergedSegmentId > 0)
      WriteDurableWatermark();
    // The domain's snapshots, then its durable timestamp: a commit that becomes durable in
    // between only makes the filter keep more.
    var snapshots = Coordinator.Snapshots.GetSnapshots();
    var durableCommitTimestamp = Coordinator.DurableTimestamp;
    return new VersionGarbageFilter<TKey, TValue>(
        this,
        Comparer,
        context.IncludesOldestRecords,
        snapshots,
        durableCommitTimestamp);
  }

  /// <summary>
  /// Makes every commit issued so far durable in the domain, then stores the durable sequence
  /// number as the column's watermark, durably, in the current mutable segment. The segments a merge
  /// writes to the disk froze earlier, so their commit records are at or below it, and the
  /// newest watermark always stays in a segment no merge has taken yet.
  /// </summary>
  void WriteDurableWatermark()
  {
    var durable = Coordinator.MakeIssuedCommitsDurable();
    Storage.Upsert(
        new VersionedKey<TKey>(VersionedKeyKind.Clock, default, CommitRecord.DurableWatermarkVersion),
        VersionedValue<TValue>.FromSystem(durable));
    Storage.SyncWALAsync().GetAwaiter().GetResult();
  }

  /// <summary>
  /// Read-only transactions end without the state lock, so every merge end also forgets
  /// what they kept.
  /// </summary>
  void OnMergeOperationEnded(
      IZoneTreeMaintenance<VersionedKey<TKey>, VersionedValue<TValue>> zoneTree,
      MergeResult mergeResult)
  {
    lock (StateLock)
    {
      PruneCommittedTransactions();
      ForgetRolledBackTransactions();
    }
  }

  public bool IsReadOnly
  {
    get => Storage.IsReadOnly;
    set => Storage.IsReadOnly = value;
  }

  public ILogger Logger => Storage.Logger;

  public ITransactionalZoneTreeMaintenance<TKey, TValue> Maintenance => this;

  public IZoneTree<VersionedKey<TKey>, VersionedValue<TValue>> ZoneTree => Storage;

  // The writing views open in this column. The domain registers the transactions, and
  // read-only views register nothing in the column, so this counts entries instead.
  public int ActiveTransactionCount
      => Entries.Values.Count(entry => entry.State == TransactionState.Active);

  public long LastCommitTimestamp => Coordinator.Sequencer.SnapshotTimestamp;

  public int AbortedTransactionCount =>
      Entries.Values.Count(x => x.State == TransactionState.Aborted);

  public int ConflictIndexCount => ConflictIndex.Count;

  public IMaintainer CreateMaintainer() => Storage.CreateMaintainer();

  public void SaveMetaData() => StorageMaintenance.SaveMetaData();

  public void Drop()
  {
    StorageMaintenance.OnMergeOperationEnded -= OnMergeOperationEnded;
    StorageMaintenance.Drop();
  }

  public void Dispose()
  {
    StorageMaintenance.OnMergeOperationEnded -= OnMergeOperationEnded;
    Storage.Dispose();
  }
}
