using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>A read-only transaction of a standalone column, over the transaction of its MVCC tree.</summary>
/// <remarks>
/// The transaction registers with its column. Dropping the column ends it, and its later
/// operations report the drop.
/// </remarks>
internal class StandaloneReadTransaction<TKey, TValue> :
    IStandaloneReadTransaction<TKey, TValue>, IClusterOpenTransaction, IColumnDependent
{
  readonly IReadTransaction<TKey, TValue> transaction;
  protected readonly ZoneTreeCluster Cluster;
  protected readonly Column Column;

  /// <summary>Rolls back a writing transaction when its timeout elapses, or null.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
      Justification = "End disposes it, and every end of the transaction, Dispose included, calls End.")]
  protected Timer ExpiryTimer;
  int Ended;

  internal StandaloneReadTransaction(Column column, IReadTransaction<TKey, TValue> transaction)
  {
    Column = column;
    Cluster = column.Cluster;
    this.transaction = transaction;
    Cluster.TransactionStarted(this);
    column.AddDependent(this);
  }

  internal StandaloneReadTransaction(Column column, IReadTransaction<TKey, TValue> transaction, TimeSpan timeout)
      : this(column, transaction)
    => ExpiryTimer = TransactionExpiry.Start(timeout, ExpireReadOnly);

  void ExpireReadOnly()
  {
    if (Volatile.Read(ref Ended) != 0)
      return;
    Dispose();
    Cluster.Logger.LogWarning("A read-only transaction of a standalone column timed out and was ended.");
  }

  /// <summary>The transaction of the MVCC tree, once the column is known not to be dropped.</summary>
  IReadTransaction<TKey, TValue> Transaction
  {
    get
    {
      Column.ThrowIfDropped();
      return transaction;
    }
  }

  public long SnapshotTimestamp => transaction.SnapshotTimestamp;

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
    if (Interlocked.Exchange(ref Ended, 1) != 0)
      return;
    ExpiryTimer?.Dispose();
    Column.RemoveDependent(this);
    Cluster.TransactionEnded(this);
  }

  public virtual void RollbackOnClose() => Dispose();

  // The MVCC transaction stops its running operations before it rolls back, so the storage
  // closes after them.
  void IColumnDependent.OnColumnDropped() => Dispose();

  public void Dispose()
  {
    try { transaction.Dispose(); }
    finally { End(); }
  }

  public ValueTask DisposeAsync()
  {
    Dispose();
    return ValueTask.CompletedTask;
  }
}
