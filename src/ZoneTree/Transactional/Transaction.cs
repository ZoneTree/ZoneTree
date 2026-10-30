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

  List<KeyRange<TKey>> ReadRanges;

  List<TransactionIterator<TKey, TValue>> OpenIterators;

  KeyList<TKey> WrittenKeys;

  KeyList<TKey> ReadKeys;

  /// <summary>
  /// True while the tree counts a writing transaction as active. Guarded by the tree state lock.
  /// </summary>
  internal bool IsRegistered;

  /// <summary>
  /// The snapshot bucket of a read-only transaction.
  /// </summary>
  internal SnapshotBucket Bucket { get; init; }

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
      ReadKeys ??= new KeyList<TKey>(Tree.KeyComparer);
      ReadKeys.Add(key);
    }
    var reader = new PointReader(this, in key);
    Tree.Storage.VisitSegments(VersionedKey<TKey>.Data(in key, long.MaxValue), ref reader);
    if (!reader.IsFound || reader.Value.Kind == VersionedValueKind.Tombstone)
    {
      value = default;
      return false;
    }
    value = reader.Value.Value;
    return true;
  }

  /// <summary>
  /// Finds the newest version of a key the transaction can see.
  /// </summary>
  /// <remarks>
  /// Segments are visited newest first, and within a segment the versions of a key
  /// come newest first. Writes go to the newest segment, and two committed writers of
  /// a key never overlap, so a visible version is never older than one in an older
  /// segment: the first visible version is the value. A deletion marker hides the
  /// copies of its version in older segments.
  /// </remarks>
  struct PointReader : ISegmentRecordVisitor<VersionedKey<TKey>, VersionedValue<TValue>>
  {
    readonly Transaction<TKey, TValue> Transaction;

    readonly TKey Key;

    List<long> DeletedVersions;

    public bool IsFound;

    public VersionedValue<TValue> Value;

    public PointReader(Transaction<TKey, TValue> transaction, in TKey key)
    {
      Transaction = transaction;
      Key = key;
    }

    public SegmentVisitAction Visit(in VersionedKey<TKey> key, in VersionedValue<TValue> value)
    {
      if (key.Kind != VersionedKeyKind.Data ||
          Transaction.Comparer.Compare(in key.Key, in Key) != 0)
        return SegmentVisitAction.NextSegment;
      if (value.Kind == VersionedValueKind.Deleted)
      {
        (DeletedVersions ??= new List<long>()).Add(key.Version);
        return SegmentVisitAction.NextRecord;
      }
      if (DeletedVersions != null && DeletedVersions.Contains(key.Version))
        return SegmentVisitAction.NextRecord;
      if (!Transaction.IsVisible(key.Version))
        return SegmentVisitAction.NextRecord;
      IsFound = true;
      Value = value;
      return SegmentVisitAction.Stop;
    }
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
    (OpenIterators ??= new()).Add(iterator);
    return iterator;
  }

  internal void OnIteratorDisposed(TransactionIterator<TKey, TValue> iterator)
  {
    OpenIterators?.Remove(iterator);
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
  internal bool HasConflict(ConflictIndex<TKey> conflictIndex)
  {
    if (conflictIndex.HasCommitAfter(WrittenKeys.Items, SnapshotTimestamp))
      return true;
    if (!TracksReads)
      return false;
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
    if (OpenIterators == null)
      return;
    for (var i = OpenIterators.Count - 1; i >= 0; --i)
      OpenIterators[i].Dispose();
  }

  public void Dispose()
  {
    Rollback();
  }
}
