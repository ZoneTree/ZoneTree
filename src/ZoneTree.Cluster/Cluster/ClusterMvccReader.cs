using ZoneTree.Transactional;
using ZoneTree.Comparers;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Cluster;

/// <summary>
/// Reads the snapshot of a transaction of <see cref="ClusterMvccZoneTree{TKey, TValue}"/>.
/// </summary>
internal abstract class ClusterMvccReader<TKey, TValue> : ColumnView,
    IReadTransaction<TKey, TValue>, IReadColumn<TKey, TValue>
{
  protected readonly ClusterMvccZoneTree<TKey, TValue> Tree;

  readonly IRefComparer<TKey> Comparer;

  List<ClusterMvccTransactionIterator<TKey, TValue>> OpenIterators;

  /// <summary>
  /// Guards the bookkeeping of the transaction: its open iterators, and the keys and
  /// ranges a writing transaction tracks. Storage reads and writes run outside of it.
  /// </summary>
  protected readonly Lock BookkeepingLock = new();

  /// <summary>
  /// The number of running operations, plus <see cref="OperationsStopped"/> once the
  /// transaction takes no new ones. Ending the transaction waits for the running
  /// operations, so none of them races with the commit, the rollback or the release
  /// of the storage iterators.
  /// </summary>
  int Operations;

  const int OperationsStopped = 1 << 30;

  /// <summary>
  /// The id of a writing transaction, or 0 for a read-only one. Its versions are
  /// the only ones above the snapshot that the transaction sees.
  /// </summary>
  public long TransactionId { get; }

  public long SnapshotTimestamp { get; }

  protected ClusterMvccReader(
      ClusterMvccZoneTree<TKey, TValue> tree,
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

  /// <summary>
  /// True until the transaction ends.
  /// </summary>
  protected abstract bool IsActive { get; }

  /// <summary>
  /// Throws for an operation that starts while an active transaction is ending.
  /// </summary>
  protected abstract void ThrowEnding();

  /// <summary>
  /// Starts an operation, which the end of the transaction waits for.
  /// Throws if the transaction ended or is ending.
  /// </summary>
  internal void BeginOperation()
  {
    // A view of a dropped column fails. Views are not registered with their column, so this
    // check is what ends them after a drop, iterators included.
    if (Column.IsDropped)
      ThrowDropped();
    if ((Interlocked.Increment(ref Operations) & OperationsStopped) == 0 && IsActive)
      return;
    Interlocked.Decrement(ref Operations);
    EnsureActive();
    ThrowEnding();
  }

  internal void EndOperation() => Interlocked.Decrement(ref Operations);

  void ThrowDropped() => throw new SchemaObjectDroppedException($"Column '{Column.Name}' was dropped.");

  /// <summary>
  /// Rejects new operations and waits for the running ones to end.
  /// An operation must not stop the operations of its own transaction.
  /// </summary>
  internal override void StopOperations()
  {
    Interlocked.Or(ref Operations, OperationsStopped);
    var wait = new SpinWait();
    while (Volatile.Read(ref Operations) != OperationsStopped)
      wait.SpinOnce();
  }

  internal bool IsVisible(long version) => Tree.IsVisible(version, this);

  public bool ContainsKey(in TKey key) => TryGet(in key, out _);

  public virtual bool TryGet(in TKey key, out TValue value)
  {
    BeginOperation();
    try
    {
      return ReadVisible(in key, out value);
    }
    finally
    {
      EndOperation();
    }
  }

  public ValueTask<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
      => RunAsync(() => ContainsKey(in key), cancellationToken);

  public ValueTask<ReadResult<TValue>> TryGetAsync(TKey key,
      CancellationToken cancellationToken = default)
      => RunAsync(() => new ReadResult<TValue>(TryGet(in key, out var value), value), cancellationToken);

  /// <summary>
  /// Reads the newest version of a key the transaction can see.
  /// </summary>
  protected bool ReadVisible(in TKey key, out TValue value)
  {
    var reader = new PointReader(this, in key);
    // Every visible version began no later than its commit timestamp, except
    // this transaction's own writes. Seek past later transaction ids instead
    // of walking all of their versions while reading an old snapshot.
    var maximumVisibleVersion = Math.Max(TransactionId, SnapshotTimestamp);
    Tree.Storage.VisitSegments(VersionedKey<TKey>.Data(in key, maximumVisibleVersion), ref reader);
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
    readonly ClusterMvccReader<TKey, TValue> Transaction;

    readonly TKey Key;

    List<long> DeletedVersions;

    public bool IsFound;

    public VersionedValue<TValue> Value;

    public PointReader(ClusterMvccReader<TKey, TValue> transaction, in TKey key)
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

  public IAsyncEnumerable<KeyValuePair<TKey, TValue>> RangeAsync(TKey from, TKey to,
      bool reverse = false, CancellationToken cancellationToken = default)
      => new AsyncRange<TKey, TValue>(Range(in from, in to, reverse), cancellationToken);

  public IAsyncTransactionIterator<TKey, TValue> CreateAsyncIterator(TransactionIteratorOptions options = null)
      => new AsyncTransactionIterator<TKey, TValue>(CreateIterator(options));

  public IAsyncTransactionIterator<TKey, TValue> CreateAsyncReverseIterator(TransactionIteratorOptions options = null)
      => new AsyncTransactionIterator<TKey, TValue>(CreateReverseIterator(options));

  ClusterMvccTransactionIterator<TKey, TValue> CreateIterator(
      bool reverse, TransactionIteratorOptions options, bool hasBound, TKey bound)
  {
    BeginOperation();
    try
    {
      var iterator = new ClusterMvccTransactionIterator<TKey, TValue>(
          this, Tree, Comparer, reverse, options, hasBound, bound);
      lock (BookkeepingLock)
        (OpenIterators ??= new()).Add(iterator);
      return iterator;
    }
    finally
    {
      EndOperation();
    }
  }

  internal void OnIteratorDisposed(ClusterMvccTransactionIterator<TKey, TValue> iterator)
  {
    lock (BookkeepingLock)
      OpenIterators?.Remove(iterator);
  }

  /// <summary>
  /// Releases the storage iterators. Open iterators hold segments that merges
  /// would otherwise delete, and they cannot be used after the transaction ends.
  /// Called after the operations stopped.
  /// </summary>
  protected void ReleaseReaders()
  {
    ClusterMvccTransactionIterator<TKey, TValue>[] iterators;
    lock (BookkeepingLock)
    {
      if (OpenIterators == null || OpenIterators.Count == 0)
        return;
      iterators = OpenIterators.ToArray();
    }
    for (var i = iterators.Length - 1; i >= 0; --i)
      iterators[i].Dispose();
  }

  // Only the views of writing domain transactions commit; they override these.
  internal override void ClaimForCommit() => throw new InvalidOperationException("A read-only view does not commit.");
  internal override bool Validate() => throw new InvalidOperationException("A read-only view does not commit.");
  internal override void Prepare(long commitTimestamp) => throw new InvalidOperationException("A read-only view does not commit.");
  internal override void WriteCommitRecord() => throw new InvalidOperationException("A read-only view does not commit.");
  internal override void Publish() => throw new InvalidOperationException("A read-only view does not commit.");
  internal override void LeaveInDoubt() => throw new InvalidOperationException("A read-only view does not commit.");
  internal override bool RollbackClaimed() => throw new InvalidOperationException("A read-only view does not commit.");

  public abstract override void Dispose();
}
