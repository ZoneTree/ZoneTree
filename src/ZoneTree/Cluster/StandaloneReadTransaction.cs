using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>A read-only transaction of a standalone column, over the transaction of its MVCC tree.</summary>
internal class StandaloneReadTransaction<TKey, TValue> :
    IStandaloneReadTransaction<TKey, TValue>, IClusterOpenTransaction
{
  readonly IReadTransaction<TKey, TValue> Transaction;
  protected readonly ZoneTreeCluster Cluster;
  int Ended;

  internal StandaloneReadTransaction(ZoneTreeCluster cluster, IReadTransaction<TKey, TValue> transaction)
  {
    Cluster = cluster;
    Transaction = transaction;
    cluster.TransactionStarted(this);
  }

  public long SnapshotTimestamp => Transaction.SnapshotTimestamp;

  public bool ContainsKey(in TKey key) => Transaction.ContainsKey(in key);

  public bool TryGet(in TKey key, out TValue value) => Transaction.TryGet(in key, out value);

  public ValueTask<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => ContainsKey(in key), cancellationToken);

  public ValueTask<ReadResult<TValue>> TryGetAsync(TKey key, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => new ReadResult<TValue>(TryGet(in key, out var value), value), cancellationToken);

  public IEnumerable<KeyValuePair<TKey, TValue>> Range(in TKey from, in TKey to, bool reverse = false)
      => Transaction.Range(in from, in to, reverse);

  public IAsyncEnumerable<KeyValuePair<TKey, TValue>> RangeAsync(TKey from, TKey to,
      bool reverse = false, CancellationToken cancellationToken = default)
      => new AsyncRange<TKey, TValue>(Range(in from, in to, reverse), cancellationToken);

  public ITransactionIterator<TKey, TValue> CreateIterator(TransactionIteratorOptions options = null)
      => Transaction.CreateIterator(options);

  public ITransactionIterator<TKey, TValue> CreateReverseIterator(TransactionIteratorOptions options = null)
      => Transaction.CreateReverseIterator(options);

  public IAsyncTransactionIterator<TKey, TValue> CreateAsyncIterator(TransactionIteratorOptions options = null)
      => new AsyncTransactionIterator<TKey, TValue>(CreateIterator(options));

  public IAsyncTransactionIterator<TKey, TValue> CreateAsyncReverseIterator(TransactionIteratorOptions options = null)
      => new AsyncTransactionIterator<TKey, TValue>(CreateReverseIterator(options));

  /// <summary>Stops tracking the transaction once it ended.</summary>
  protected void End()
  {
    if (Interlocked.Exchange(ref Ended, 1) == 0)
      Cluster.TransactionEnded(this);
  }

  public virtual void RollbackOnClose() => Dispose();

  public void Dispose()
  {
    try { Transaction.Dispose(); }
    finally { End(); }
  }

  public ValueTask DisposeAsync()
  {
    Dispose();
    return ValueTask.CompletedTask;
  }
}
