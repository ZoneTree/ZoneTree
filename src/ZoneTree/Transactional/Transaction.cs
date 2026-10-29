using ZoneTree.Comparers;
using ZoneTree.Exceptions;

namespace ZoneTree.Transactional;

/// <summary>
/// A transaction of <see cref="TransactionalZoneTree{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// Writes go to the storage tree at once, stored under the transaction id, and stay
/// invisible to other transactions until the commit. The transaction keeps the keys
/// it wrote to detect conflicts and to roll back, and in serializable mode the keys
/// and ranges it read.
/// </remarks>
internal sealed class Transaction<TKey, TValue> : ITransaction<TKey, TValue>
{
  readonly TransactionalZoneTree<TKey, TValue> Tree;

  readonly IRefComparer<TKey> Comparer;

  readonly List<KeyRange<TKey>> ReadRanges = new();

  readonly List<TransactionIterator<TKey, TValue>> OpenIterators = new();

  SortedSet<TKey> WrittenKeys;

  SortedSet<TKey> ReadKeys;

  IZoneTreeIterator<VersionedKey<TKey>, VersionedValue<TValue>> PointReader;

  TransactionState state;

  public long TransactionId { get; }

  public long SnapshotTimestamp { get; }

  public long CommitTimestamp { get; private set; }

  public TransactionIsolationLevel IsolationLevel { get; }

  public bool IsReadOnly { get; }

  public TransactionState State => state;

  /// <summary>
  /// The clock when the transaction began. Rolled back versions stay registered
  /// until every transaction that began before their deletion has ended.
  /// </summary>
  public long BeginSequence { get; }

  /// <summary>
  /// The entry of a writing transaction, or null for a read-only transaction.
  /// </summary>
  public TransactionEntry Entry { get; }

  public bool HasWrites => WrittenKeys != null;

  /// <summary>
  /// True if the commit validates the reads. Read-only transactions read a
  /// consistent snapshot and never need validation.
  /// </summary>
  internal bool TracksReads =>
      IsolationLevel == TransactionIsolationLevel.Serializable && !IsReadOnly;

  public Transaction(
      TransactionalZoneTree<TKey, TValue> tree,
      IRefComparer<TKey> comparer,
      TransactionEntry entry,
      long beginSequence,
      long snapshotTimestamp,
      TransactionIsolationLevel isolationLevel)
  {
    Tree = tree;
    Comparer = comparer;
    Entry = entry;
    TransactionId = entry?.Id ?? 0;
    IsReadOnly = entry == null;
    BeginSequence = beginSequence;
    SnapshotTimestamp = snapshotTimestamp;
    IsolationLevel = isolationLevel;
  }

  internal void EnsureActive()
  {
    switch (state)
    {
      case TransactionState.Committed:
        throw new TransactionAlreadyCommittedException(TransactionId);
      case TransactionState.Aborted:
        throw new TransactionAbortedException(TransactionId);
    }
  }

  internal bool IsVisible(long version) => Tree.IsVisible(version, this);

  public bool ContainsKey(in TKey key) => TryGet(in key, out _);

  public bool TryGet(in TKey key, out TValue value)
  {
    EnsureActive();
    if (TracksReads)
    {
      ReadKeys ??= new SortedSet<TKey>(new ComparerFromRefComparer<TKey>(Comparer));
      ReadKeys.Add(key);
    }
    PointReader ??= Tree.CreateStorageIterator(
        reverse: false,
        contributeToTheBlockCache: true,
        diskSegmentPrefetchSize: 0);
    var reader = PointReader;
    reader.Seek(VersionedKey<TKey>.Data(in key, long.MaxValue));
    while (reader.Next())
    {
      var storedKey = reader.CurrentKey;
      if (storedKey.Kind != VersionedKeyKind.Data ||
          Comparer.Compare(in storedKey.Key, in key) != 0)
        break;
      // Versions come newest first, and the newest visible one is the value.
      if (!IsVisible(storedKey.Version))
        continue;
      var storedValue = reader.CurrentValue;
      if (storedValue.Kind == VersionedValueKind.Tombstone)
        break;
      value = storedValue.Value;
      return true;
    }
    value = default;
    return false;
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
    if (IsReadOnly)
      throw new TransactionIsReadOnlyException(TransactionId);
    // A key committed after the snapshot fails the commit anyway, so stop early.
    if (Tree.HasCommitAfter(in key, SnapshotTimestamp))
    {
      Rollback();
      throw new TransactionConflictException(TransactionId);
    }
    try
    {
      if (WrittenKeys == null)
      {
        WrittenKeys = new SortedSet<TKey>(new ComparerFromRefComparer<TKey>(Comparer));
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

  public ITransactionIterator<TKey, TValue> CreateIterator(TransactionIteratorOptions options = null)
  {
    return CreateIterator(false, options);
  }

  public ITransactionIterator<TKey, TValue> CreateReverseIterator(TransactionIteratorOptions options = null)
  {
    return CreateIterator(true, options);
  }

  TransactionIterator<TKey, TValue> CreateIterator(bool reverse, TransactionIteratorOptions options)
  {
    EnsureActive();
    var iterator = new TransactionIterator<TKey, TValue>(this, Tree, Comparer, reverse, options);
    OpenIterators.Add(iterator);
    return iterator;
  }

  internal void OnIteratorDisposed(TransactionIterator<TKey, TValue> iterator)
  {
    OpenIterators.Remove(iterator);
  }

  /// <summary>
  /// Adds a range to the read set and returns its index.
  /// </summary>
  internal int AddReadRange(in KeyRange<TKey> range)
  {
    ReadRanges.Add(range);
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
  internal bool HasConflict(ConflictIndex<TKey> conflictIndex)
  {
    if (conflictIndex.HasCommitAfter(WrittenKeys, SnapshotTimestamp))
      return true;
    if (!TracksReads)
      return false;
    if (ReadKeys != null && conflictIndex.HasCommitAfter(ReadKeys, SnapshotTimestamp))
      return true;
    return ReadRanges.Count > 0 &&
        conflictIndex.HasCommitAfter(ReadRanges, SnapshotTimestamp);
  }

  internal TKey[] GetWrittenKeys()
  {
    var keys = new TKey[WrittenKeys.Count];
    WrittenKeys.CopyTo(keys);
    return keys;
  }

  internal IEnumerable<TKey> WrittenKeysForRollback => WrittenKeys;

  public void Commit()
  {
    if (!TryCommit())
      throw new TransactionConflictException(TransactionId);
  }

  public bool TryCommit()
  {
    EnsureActive();
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

  public void Rollback()
  {
    if (state != TransactionState.Active)
      return;
    state = TransactionState.Aborted;
    ReleaseReaders();
    Tree.Rollback(this);
  }

  /// <summary>
  /// Releases the storage iterators. Open iterators hold segments that merges
  /// would otherwise delete, and they cannot be used after the transaction ends.
  /// </summary>
  void ReleaseReaders()
  {
    PointReader?.Dispose();
    PointReader = null;
    for (var i = OpenIterators.Count - 1; i >= 0; --i)
      OpenIterators[i].Dispose();
  }

  public void Dispose()
  {
    Rollback();
  }
}
