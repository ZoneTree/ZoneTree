using ZoneTree.Comparers;
using ZoneTree.Exceptions;
using ZoneTree.Transactional.Conflicts;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Transactional.Distributed;

/// <summary>
/// A transaction of <see cref="DistributedMvccZoneTree{TKey, TValue}"/> that can write.
/// </summary>
/// <remarks>
/// Writes go to the storage tree at once, stored under the transaction id, and stay
/// invisible to other transactions until the commit. The transaction keeps the keys
/// it wrote to detect conflicts and to roll back, the keys it read for update, and in
/// serializable mode the keys and ranges it read.
/// </remarks>
internal sealed class DistributedMvccTransaction<TKey, TValue> :
    DistributedMvccReader<TKey, TValue>, ITransaction<TKey, TValue>, IDistributedWriteColumn<TKey, TValue>
{
  List<KeyRange<TKey>> ReadRanges;

  KeyList<TKey> WrittenKeys;

  KeyList<TKey> ReadKeys;

  /// <summary>
  /// True while the tree counts the transaction as active. Guarded by the tree state lock.
  /// </summary>
  internal bool IsRegistered;

  volatile TransactionState state;

  public long CommitTimestamp { get; private set; }

  public TransactionIsolationLevel IsolationLevel { get; }

  public TransactionState State => state;

  public bool HasConflict { get; private set; }

  /// <summary>
  /// The clock when the transaction began. Rolled back versions stay registered
  /// until every transaction that began before their deletion has ended.
  /// </summary>
  public long BeginSequence => TransactionId;

  public DistributedTransactionEntry Entry { get; }

  internal override bool HasWrites => WrittenKeys != null;
  internal override bool ConflictDetected => HasConflict;
  internal override bool CanCommit() => Tree.CanCommit(this);

  /// <summary>
  /// True if the commit validates every read.
  /// </summary>
  internal bool TracksReads => IsolationLevel == TransactionIsolationLevel.Serializable;

  public DistributedMvccTransaction(
      DistributedMvccZoneTree<TKey, TValue> tree,
      IRefComparer<TKey> comparer,
      DistributedTransactionEntry entry,
      long snapshotTimestamp,
      TransactionIsolationLevel isolationLevel)
      : base(tree, comparer, entry.Id, snapshotTimestamp)
  {
    Entry = entry;
    IsolationLevel = isolationLevel;
  }

  internal override void EnsureActive()
  {
    switch (state)
    {
      case TransactionState.Committed:
        throw new TransactionAlreadyCommittedException(TransactionId);
      case TransactionState.Aborted:
        throw new TransactionAbortedException(TransactionId);
      case TransactionState.Committing:
        throw new InvalidOperationException("The transaction is waiting for its commit to be published.");
      case TransactionState.InDoubt:
        throw new InvalidOperationException("The transaction's commit outcome is uncertain after a sync failure.");
    }
  }

  public override bool TryGet(in TKey key, out TValue value)
  {
    EnsureActive();
    if (TracksReads)
      AddReadKey(in key);
    return ReadVisible(in key, out value);
  }

  public bool TryGetForUpdate(in TKey key, out TValue value)
  {
    EnsureActive();
    AddReadKey(in key);
    return ReadVisible(in key, out value);
  }

  public ValueTask<DistributedReadResult<TValue>> TryGetForUpdateAsync(TKey key,
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var found = TryGetForUpdate(in key, out var value);
    return new(new DistributedReadResult<TValue>(found, value));
  }

  void AddReadKey(in TKey key)
  {
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
  {
    cancellationToken.ThrowIfCancellationRequested();
    Upsert(in key, in value);
    return ValueTask.CompletedTask;
  }

  public ValueTask DeleteAsync(TKey key, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    Delete(in key);
    return ValueTask.CompletedTask;
  }

  void Write(in TKey key, in VersionedValue<TValue> value)
  {
    EnsureActive();
    // The commit of a conflicting transaction fails, so its writes are not stored.
    if (HasConflict)
      return;
    if (Tree.HasCommitAfter(in key, SnapshotTimestamp))
    {
      HasConflict = true;
      return;
    }
    try
    {
      if (WrittenKeys == null)
      {
        WrittenKeys = new KeyList<TKey>(Tree.KeyComparer);
        Tree.OnFirstWrite(this);
      }
      WrittenKeys.Add(key);
      Tree.Storage.Upsert(VersionedKey<TKey>.Data(in key, TransactionId), in value);
    }
    catch
    {
      // The storage tree can keep a write in memory that its WAL never recorded,
      // so the transaction must not commit it.
      Rollback();
      throw;
    }
  }

  /// <summary>
  /// Adds a range to the read set and returns its index.
  /// </summary>
  internal int AddReadRange(in KeyRange<TKey> range)
  {
    (ReadRanges ??= new()).Add(range);
    return ReadRanges.Count - 1;
  }

  internal void UpdateReadRange(int index, in KeyRange<TKey> range)
  {
    ReadRanges[index] = range;
  }

  /// <summary>
  /// Checks the transaction against the commits after its snapshot.
  /// Called by the tree under its commit lock.
  /// </summary>
  internal bool ConflictsWith(ConflictIndex<TKey> conflictIndex)
  {
    if (conflictIndex.HasCommitAfter(WrittenKeys.Items, SnapshotTimestamp))
      return true;
    if (ReadKeys != null && conflictIndex.HasCommitAfter(ReadKeys.Items, SnapshotTimestamp))
      return true;
    return ReadRanges != null &&
        conflictIndex.HasCommitAfter(ReadRanges, SnapshotTimestamp);
  }

  internal TKey[] GetWrittenKeys()
  {
    return WrittenKeys.ToDistinctArray();
  }

  internal IEnumerable<TKey> WrittenKeysForRollback => WrittenKeys.Items;

  public void Commit()
  {
    if (!TryCommit())
      throw new TransactionConflictException(TransactionId);
  }

  public bool TryCommit()
  {
    EnsureActive();
    if (HasConflict)
    {
      Rollback();
      return false;
    }
    ReleaseReaders();
    bool isCommitted;
    try
    {
      isCommitted = Tree.Commit(this);
    }
    catch
    {
      // A failure before the commit point rolled the transaction back,
      // and one after it leaves the transaction committed.
      if (state == TransactionState.Active)
        state = TransactionState.Aborted;
      throw;
    }
    if (!isCommitted)
      state = TransactionState.Aborted;
    return isCommitted;
  }

  /// <summary>
  /// Marks the transaction committed. Called by the tree at the commit point.
  /// </summary>
  internal void OnCommitted(long commitTimestamp)
  {
    CommitTimestamp = commitTimestamp;
    state = TransactionState.Committed;
  }

  internal override Task<bool> BeginCommit(bool durable)
  {
    EnsureActive();
    if (HasConflict)
    {
      Rollback();
      return Task.FromResult(false);
    }
    ReleaseReaders();
    try
    {
      var commit = durable && HasWrites ? Tree.CommitDurableAsync(this) : Tree.CommitAsync(this);
      if (commit.IsCompletedSuccessfully)
      {
        if (!GetCompletedResult(commit))
          state = TransactionState.Aborted;
        return commit;
      }
      return CompleteDatabaseCommitAsync(commit);
    }
    catch
    {
      if (state == TransactionState.Active)
        state = TransactionState.Aborted;
      throw;
    }
  }

  static bool GetCompletedResult(Task<bool> commit) => commit.GetAwaiter().GetResult();

  async Task<bool> CompleteDatabaseCommitAsync(Task<bool> commit)
  {
    try
    {
      var committed = await commit.ConfigureAwait(false);
      if (!committed)
        state = TransactionState.Aborted;
      return committed;
    }
    catch
    {
      if (state == TransactionState.Active)
        state = TransactionState.Aborted;
      throw;
    }
  }

  public async Task CommitDurableAsync(CancellationToken cancellationToken = default)
  {
    if (!await TryCommitDurableAsync(cancellationToken).ConfigureAwait(false))
      throw new TransactionConflictException(TransactionId);
  }

  public async Task<bool> TryCommitDurableAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    EnsureActive();
    if (HasConflict)
    {
      Rollback();
      return false;
    }
    ReleaseReaders();
    try
    {
      // Cancellation stops this wait, not the sync or its eventual publication.
      var committed = await Tree.CommitDurableAsync(this).WaitAsync(cancellationToken).ConfigureAwait(false);
      if (!committed)
        state = TransactionState.Aborted;
      return committed;
    }
    catch
    {
      if (state == TransactionState.Active)
        state = TransactionState.Aborted;
      throw;
    }
  }

  internal void OnCommitting() => state = TransactionState.Committing;

  internal void OnCommitInDoubt() => state = TransactionState.InDoubt;

  public void Rollback()
  {
    if (state != TransactionState.Active)
      return;
    state = TransactionState.Aborted;
    ReleaseReaders();
    Tree.Rollback(this);
  }

  public override void Dispose()
  {
    Rollback();
  }
}
