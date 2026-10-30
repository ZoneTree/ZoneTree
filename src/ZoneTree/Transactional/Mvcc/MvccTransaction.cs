using ZoneTree.Comparers;
using ZoneTree.Exceptions;
using ZoneTree.Transactional.Conflicts;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Transactional.Mvcc;

/// <summary>
/// A transaction of <see cref="MvccZoneTree{TKey, TValue}"/> that can write.
/// </summary>
/// <remarks>
/// Writes go to the storage tree at once, stored under the transaction id, and stay
/// invisible to other transactions until the commit. The transaction keeps the keys
/// it wrote to detect conflicts and to roll back, the keys it read for update, and in
/// serializable mode the keys and ranges it read.
/// </remarks>
internal sealed class MvccTransaction<TKey, TValue> :
    MvccReader<TKey, TValue>, ITransaction<TKey, TValue>
{
  List<KeyRange<TKey>> ReadRanges;

  KeyList<TKey> WrittenKeys;

  KeyList<TKey> ReadKeys;

  /// <summary>
  /// True while the tree counts the transaction as active. Guarded by the tree state lock.
  /// </summary>
  internal bool IsRegistered;

  TransactionState state;

  public long CommitTimestamp { get; private set; }

  public TransactionIsolationLevel IsolationLevel { get; }

  public TransactionState State => state;

  public bool HasConflict { get; private set; }

  /// <summary>
  /// The clock when the transaction began. Rolled back versions stay registered
  /// until every transaction that began before their deletion has ended.
  /// </summary>
  public long BeginSequence => TransactionId;

  public TransactionEntry Entry { get; }

  public bool HasWrites => WrittenKeys != null;

  /// <summary>
  /// True if the commit validates every read.
  /// </summary>
  internal bool TracksReads => IsolationLevel == TransactionIsolationLevel.Serializable;

  public MvccTransaction(
      MvccZoneTree<TKey, TValue> tree,
      IRefComparer<TKey> comparer,
      TransactionEntry entry,
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
    long commitTimestamp;
    try
    {
      isCommitted = Tree.Commit(this, out commitTimestamp);
    }
    catch
    {
      // The tree rolled the transaction back before throwing.
      state = TransactionState.Aborted;
      throw;
    }
    if (!isCommitted)
    {
      state = TransactionState.Aborted;
      return false;
    }
    CommitTimestamp = commitTimestamp;
    state = TransactionState.Committed;
    return true;
  }

  public async Task CommitDurableAsync(CancellationToken cancellationToken = default)
  {
    Commit();
    await Tree.SyncWALAsync(cancellationToken).ConfigureAwait(false);
  }

  public async Task<bool> TryCommitDurableAsync(CancellationToken cancellationToken = default)
  {
    if (!TryCommit())
      return false;
    await Tree.SyncWALAsync(cancellationToken).ConfigureAwait(false);
    return true;
  }

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
