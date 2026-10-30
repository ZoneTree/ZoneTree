using ZoneTree.Comparers;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Transactional.Mvcc;

/// <summary>
/// Reads the snapshot of a transaction of <see cref="MvccZoneTree{TKey, TValue}"/>.
/// </summary>
internal abstract class MvccReader<TKey, TValue> : IReadTransaction<TKey, TValue>
{
  protected readonly MvccZoneTree<TKey, TValue> Tree;

  readonly IRefComparer<TKey> Comparer;

  List<MvccTransactionIterator<TKey, TValue>> OpenIterators;

  /// <summary>
  /// The id of a writing transaction, or 0 for a read-only one. Its versions are
  /// the only ones above the snapshot that the transaction sees.
  /// </summary>
  public long TransactionId { get; }

  public long SnapshotTimestamp { get; }

  protected MvccReader(
      MvccZoneTree<TKey, TValue> tree,
      IRefComparer<TKey> comparer,
      long transactionId,
      long snapshotTimestamp)
  {
    Tree = tree;
    Comparer = comparer;
    TransactionId = transactionId;
    SnapshotTimestamp = snapshotTimestamp;
  }

  /// <summary>
  /// Throws if the transaction ended.
  /// </summary>
  internal abstract void EnsureActive();

  internal bool IsVisible(long version) => Tree.IsVisible(version, this);

  public bool ContainsKey(in TKey key) => TryGet(in key, out _);

  public virtual bool TryGet(in TKey key, out TValue value)
  {
    EnsureActive();
    return ReadVisible(in key, out value);
  }

  /// <summary>
  /// Reads the newest version of a key the transaction can see.
  /// </summary>
  protected bool ReadVisible(in TKey key, out TValue value)
  {
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
    readonly MvccReader<TKey, TValue> Transaction;

    readonly TKey Key;

    List<long> DeletedVersions;

    public bool IsFound;

    public VersionedValue<TValue> Value;

    public PointReader(MvccReader<TKey, TValue> transaction, in TKey key)
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

  public IEnumerable<KeyValuePair<TKey, TValue>> Range(
      in TKey from, in TKey to, bool reverse = false)
  {
    EnsureActive();
    return EnumerateRange(from, to, reverse);
  }

  IEnumerable<KeyValuePair<TKey, TValue>> EnumerateRange(TKey from, TKey to, bool reverse)
  {
    if (Comparer.Compare(in from, in to) > 0)
      yield break;
    // The iterator stops at the far end of the range, so the range it
    // adds to the read set ends there too.
    using var iterator = CreateIterator(reverse, null, true, reverse ? from : to);
    iterator.Seek(reverse ? to : from);
    while (iterator.Next())
      yield return iterator.Current;
  }

  public ITransactionIterator<TKey, TValue> CreateIterator(TransactionIteratorOptions options = null)
  {
    return CreateIterator(false, options, false, default);
  }

  public ITransactionIterator<TKey, TValue> CreateReverseIterator(TransactionIteratorOptions options = null)
  {
    return CreateIterator(true, options, false, default);
  }

  MvccTransactionIterator<TKey, TValue> CreateIterator(
      bool reverse, TransactionIteratorOptions options, bool hasBound, TKey bound)
  {
    EnsureActive();
    var iterator = new MvccTransactionIterator<TKey, TValue>(
        this, Tree, Comparer, reverse, options, hasBound, bound);
    (OpenIterators ??= new()).Add(iterator);
    return iterator;
  }

  internal void OnIteratorDisposed(MvccTransactionIterator<TKey, TValue> iterator)
  {
    OpenIterators?.Remove(iterator);
  }

  /// <summary>
  /// Releases the storage iterators. Open iterators hold segments that merges
  /// would otherwise delete, and they cannot be used after the transaction ends.
  /// </summary>
  protected void ReleaseReaders()
  {
    if (OpenIterators == null)
      return;
    for (var i = OpenIterators.Count - 1; i >= 0; --i)
      OpenIterators[i].Dispose();
  }

  public abstract void Dispose();
}
