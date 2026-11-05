using ZoneTree.Transactional;
using System.Runtime.ExceptionServices;
using ZoneTree.Comparers;
using ZoneTree.Exceptions;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Cluster;

/// <summary>
/// The view of a writing domain transaction in the tree of a coordinated column.
/// </summary>
/// <remarks>
/// Writes go to the storage tree at once, stored under the transaction id, and stay
/// invisible to other transactions until the commit. The view keeps the keys it wrote to
/// detect conflicts and to roll back, the keys it read for update, and in serializable mode
/// the keys and ranges it read. Its operations can run concurrently. Only its domain
/// transaction commits it: the domain claims the end of every view, waits for the running
/// operations, validates, prepares and publishes the views together.
/// </remarks>
internal sealed class ClusterMvccTransaction<TKey, TValue> :
    ClusterMvccReader<TKey, TValue>, IWriteColumn<TKey, TValue>
{
  List<KeyRange<TKey>> ReadRanges;

  KeyList<TKey> WrittenKeys;

  KeyList<TKey> ReadKeys;

  /// <summary>
  /// True once the transaction status record is stored. Guarded by the bookkeeping lock.
  /// </summary>
  bool IsStatusStored;

  /// <summary>
  /// True once a write failed in the storage tree. The view is rolled back, and the
  /// domain commit that waited for the write fails.
  /// </summary>
  volatile bool hasFailedWrite;

  volatile TransactionState state;

  volatile bool hasConflict;

  /// <summary>
  /// Who owns the end of the view: <see cref="EndedByCommit"/>,
  /// <see cref="EndedByRollback"/>, or 0 while nobody does.
  /// </summary>
  int EndOwner;

  const int EndedByCommit = 1;

  const int EndedByRollback = 2;

  public long CommitTimestamp { get; private set; }

  public TransactionIsolationLevel IsolationLevel { get; }

  public TransactionState State => state;

  public bool HasConflict => hasConflict;

  internal override bool HasFailedWrite => hasFailedWrite;

  public TransactionEntry Entry { get; }

  internal override bool HasWrites => WrittenKeys != null;
  internal override bool NeedsValidation => WrittenKeys != null || ReadKeys != null || ReadRanges != null;
  internal override bool ConflictDetected => HasConflict;

  /// <summary>
  /// True if the commit validates every read.
  /// </summary>
  internal bool TracksReads => IsolationLevel == TransactionIsolationLevel.Serializable;

  public ClusterMvccTransaction(
      ClusterMvccZoneTree<TKey, TValue> tree,
      IRefComparer<TKey> comparer,
      TransactionEntry entry,
      long snapshotTimestamp,
      TransactionIsolationLevel isolationLevel)
      : base(tree, comparer, entry.Id, snapshotTimestamp)
  {
    Entry = entry;
    IsolationLevel = isolationLevel;
  }

  protected override bool IsActive => state == TransactionState.Active;

  internal override void EnsureActive()
  {
    switch (state)
    {
      case TransactionState.Committed:
        throw new TransactionAlreadyCommittedException(TransactionId);
      case TransactionState.Aborted:
        throw new TransactionAbortedException(TransactionId);
      case TransactionState.Committing:
        throw new TransactionCommitInProgressException(TransactionId);
      case TransactionState.InDoubt:
        throw new TransactionInDoubtException(TransactionId);
    }
  }

  protected override void ThrowEnding()
  {
    if (Volatile.Read(ref EndOwner) == EndedByRollback)
      throw new TransactionAbortedException(TransactionId);
    throw new TransactionCommitInProgressException(TransactionId);
  }

  public override bool TryGet(in TKey key, out TValue value)
  {
    BeginOperation();
    try
    {
      if (TracksReads)
        AddReadKey(in key);
      return ReadVisible(in key, out value);
    }
    finally
    {
      EndOperation();
    }
  }

  public bool TryGetForUpdate(in TKey key, out TValue value)
  {
    BeginOperation();
    try
    {
      AddReadKey(in key);
      return ReadVisible(in key, out value);
    }
    finally
    {
      EndOperation();
    }
  }

  public ValueTask<ReadResult<TValue>> TryGetForUpdateAsync(TKey key,
      CancellationToken cancellationToken = default)
      => RunAsync(() => new ReadResult<TValue>(TryGetForUpdate(in key, out var value), value), cancellationToken);

  void AddReadKey(in TKey key)
  {
    lock (BookkeepingLock)
      (ReadKeys ??= new KeyList<TKey>(Tree.KeyComparer)).Add(key);
  }

  public void Upsert(in TKey key, in TValue value)
  {
    Write(in key, VersionedValue<TValue>.FromValue(in value));
  }

  public void Delete(in TKey key)
  {
    Write(in key, VersionedValue<TValue>.Tombstone);
  }

  public ValueTask UpsertAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
      => RunAsync(() => Upsert(in key, in value), cancellationToken);

  public ValueTask DeleteAsync(TKey key, CancellationToken cancellationToken = default)
      => RunAsync(() => Delete(in key), cancellationToken);

  void Write(in TKey key, in VersionedValue<TValue> value)
  {
    BeginOperation();
    Exception failure = null;
    try
    {
      // The commit of a conflicting transaction fails, so its writes are not stored.
      if (hasConflict)
        return;
      if (Tree.HasCommitAfter(in key, SnapshotTimestamp))
      {
        hasConflict = true;
        return;
      }
      try
      {
        lock (BookkeepingLock)
        {
          if (WrittenKeys == null)
          {
            WrittenKeys = new KeyList<TKey>(Tree.KeyComparer);
            Tree.OnFirstWrite(this);
            IsStatusStored = true;
          }
          // A version without its status record would look committed after a crash.
          else if (!IsStatusStored)
            throw new TransactionAbortedException(TransactionId);
          WrittenKeys.Add(key);
        }
        Tree.Storage.Upsert(VersionedKey<TKey>.Data(in key, TransactionId), in value);
      }
      catch (Exception e)
      {
        hasFailedWrite = true;
        failure = e;
      }
    }
    finally
    {
      EndOperation();
    }
    if (failure == null)
      return;
    // The storage tree can keep a write in memory that its WAL never recorded,
    // so the transaction must not commit it. The rollback waits for the running
    // operations, so this one ends first.
    Rollback();
    ExceptionDispatchInfo.Throw(failure);
  }

  /// <summary>
  /// Adds a range to the read set and returns its index.
  /// </summary>
  internal int AddReadRange(in KeyRange<TKey> range)
  {
    lock (BookkeepingLock)
    {
      (ReadRanges ??= new()).Add(range);
      return ReadRanges.Count - 1;
    }
  }

  internal void UpdateReadRange(int index, in KeyRange<TKey> range)
  {
    lock (BookkeepingLock)
      ReadRanges[index] = range;
  }

  /// <summary>
  /// Checks the view against the commits after its snapshot: the keys it wrote, and the
  /// keys and ranges it read. Called by the tree under its commit lock, after the
  /// operations stopped.
  /// </summary>
  internal bool ConflictsWith(ConflictIndex<TKey> conflictIndex)
  {
    if (WrittenKeys != null && conflictIndex.HasCommitAfter(WrittenKeys.Items, SnapshotTimestamp))
      return true;
    if (ReadKeys != null && conflictIndex.HasCommitAfter(ReadKeys.Items, SnapshotTimestamp))
      return true;
    return ReadRanges != null &&
        conflictIndex.HasCommitAfter(ReadRanges, SnapshotTimestamp);
  }

  /// <summary>
  /// The distinct written keys in order, sorted when the commit claims the view, before it
  /// takes the commit gates.
  /// </summary>
  internal TKey[] CommitKeys { get; private set; }

  internal IEnumerable<TKey> WrittenKeysForRollback => WrittenKeys.Items;

  internal override void ClaimForCommit()
  {
    EnsureActive();
    if (Interlocked.CompareExchange(ref EndOwner, EndedByCommit, 0) != 0)
    {
      EnsureActive();
      ThrowEnding();
    }
    StopOperations();
    ReleaseReaders();
    if (WrittenKeys != null)
      CommitKeys = WrittenKeys.ToDistinctArray();
  }

  internal override bool Validate() => Tree.Validate(this);

  internal override void Prepare(long commitTimestamp) => Tree.Prepare(this, commitTimestamp);

  internal override void WriteCommitRecord() => Tree.WriteCommitRecord(this);

  internal override void Publish()
  {
    if (HasWrites)
      Tree.Publish(this);
    else
      Tree.EndUnwritten(this);
  }

  internal override void LeaveInDoubt()
  {
    if (HasWrites)
      Tree.LeaveInDoubt(this);
    else
      Tree.EndUnwritten(this);
  }

  /// <summary>
  /// True once the view may have written its commit record. Its versions then look committed
  /// to recovery unless the rollback deletes them.
  /// </summary>
  internal bool HasCommitRecord;

  // Without a commit record, the pending status keeps remaining versions aborted.
  internal override bool RollbackClaimed() => RollbackOwned() || !HasCommitRecord;

  /// <summary>
  /// Marks the view committed. Called by the tree when it publishes the view.
  /// </summary>
  internal void OnCommitted(long commitTimestamp)
  {
    CommitTimestamp = commitTimestamp;
    state = TransactionState.Committed;
  }

  internal void OnCommitting() => state = TransactionState.Committing;

  internal void OnCommitInDoubt() => state = TransactionState.InDoubt;

  /// <summary>
  /// Rolls back an active view. A commit or another rollback that owns the end of the
  /// view makes it a no-op.
  /// </summary>
  public void Rollback()
  {
    if (Interlocked.CompareExchange(ref EndOwner, EndedByRollback, 0) != 0)
      return;
    RollbackOwned();
  }

  bool RollbackOwned()
  {
    state = TransactionState.Aborted;
    StopOperations();
    ReleaseReaders();
    return Tree.Rollback(this);
  }

  public override void Dispose()
  {
    Rollback();
  }
}
