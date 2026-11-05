using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>A read-only transaction of a standalone column, over the transaction of its MVCC tree.</summary>
/// <remarks>
/// Beginning and ending the transaction share no state with other threads beyond what the
/// MVCC tree does. Its operations report a drop of the column. It registers with the column
/// only when it opens an iterator or a range: the MVCC tree's iterators know nothing of the
/// column, so a drop ends the transaction, which ends them. A closing cluster leaves a
/// read-only transaction to fail on the closed tree.
/// </remarks>
internal class StandaloneReadTransaction<TKey, TValue> : IStandaloneReadTransaction<TKey, TValue>, IColumnDependent
{
  readonly IReadTransaction<TKey, TValue> transaction;
  protected readonly ZoneTreeCluster Cluster;
  protected readonly Column Column;

  /// <summary>Rolls back a writing transaction when its timeout elapses, or null.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
      Justification = "End disposes it, and every end of the transaction, Dispose included, calls End.")]
  protected Timer ExpiryTimer;
  int Ended;

  /// <summary>1 once the transaction registered with its column.</summary>
  int Registered;

  internal StandaloneReadTransaction(Column column, IReadTransaction<TKey, TValue> transaction)
  {
    Column = column;
    Cluster = column.Cluster;
    this.transaction = transaction;
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
  {
    RegisterForIterators();
    return Transaction.Range(in from, in to, reverse);
  }

  public IAsyncEnumerable<KeyValuePair<TKey, TValue>> RangeAsync(TKey from, TKey to,
      bool reverse = false, CancellationToken cancellationToken = default)
      => new AsyncRange<TKey, TValue>(Range(in from, in to, reverse), cancellationToken);

  public ITransactionIterator<TKey, TValue> CreateIterator(TransactionIteratorOptions options = null)
  {
    RegisterForIterators();
    return Transaction.CreateIterator(options);
  }

  public ITransactionIterator<TKey, TValue> CreateReverseIterator(TransactionIteratorOptions options = null)
  {
    RegisterForIterators();
    return Transaction.CreateReverseIterator(options);
  }

  /// <summary>
  /// Registers the transaction with its column before its first iterator, so a drop ends the
  /// transaction and its iterators before the storage goes. The column's operation gate makes
  /// a drop wait for a registration that runs, or makes the registration fail after it.
  /// </summary>
  void RegisterForIterators()
  {
    if (Volatile.Read(ref Registered) != 0 || Interlocked.Exchange(ref Registered, 1) != 0)
      return;
    Column.BeginOperation();
    try
    {
      Column.AddDependent(this);
    }
    finally
    {
      Column.EndOperation();
    }
  }

  public IAsyncTransactionIterator<TKey, TValue> CreateAsyncIterator(TransactionIteratorOptions options = null)
      => new AsyncTransactionIterator<TKey, TValue>(CreateIterator(options));

  public IAsyncTransactionIterator<TKey, TValue> CreateAsyncReverseIterator(TransactionIteratorOptions options = null)
      => new AsyncTransactionIterator<TKey, TValue>(CreateReverseIterator(options));

  /// <summary>Stops the timer once the transaction ended.</summary>
  protected void End()
  {
    if (Interlocked.Exchange(ref Ended, 1) != 0)
      return;
    ExpiryTimer?.Dispose();
    if (Volatile.Read(ref Registered) != 0)
      Column.RemoveDependent(this);
    OnEnded();
  }

  // A drop ends a transaction that opened iterators while the tree still exists, so the MVCC
  // transaction stops its operations and releases its iterators first.
  void IColumnDependent.OnColumnDropped()
  {
    try { transaction.Dispose(); }
    finally { End(); }
  }

  /// <summary>Called once, when the transaction ended.</summary>
  protected virtual void OnEnded()
  {
  }

  public void Dispose()
  {
    try
    {
      // A dropped column's tree is gone, and with it what the transaction kept.
      if (!Column.IsDropped)
        transaction.Dispose();
    }
    finally
    {
      End();
    }
  }

  public ValueTask DisposeAsync()
  {
    Dispose();
    return ValueTask.CompletedTask;
  }
}
