namespace ZoneTree.Transactional;

/// <summary>
/// A transaction of a transactional ZoneTree.
/// </summary>
/// <remarks>
/// A transaction reads the snapshot taken when it began, plus its own writes.
/// It is not thread-safe. Disposing an active transaction rolls it back.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface ITransaction<TKey, TValue> : IDisposable
{
  /// <summary>
  /// The transaction id. Read-only transactions have no id and return 0.
  /// </summary>
  long TransactionId { get; }

  /// <summary>
  /// The timestamp of the snapshot the transaction reads:
  /// the commit timestamp of the latest commit it sees.
  /// </summary>
  long SnapshotTimestamp { get; }

  /// <summary>
  /// The commit timestamp, or 0 until the transaction commits.
  /// A transaction that wrote nothing commits at its snapshot timestamp.
  /// </summary>
  long CommitTimestamp { get; }

  /// <summary>
  /// The isolation level.
  /// </summary>
  TransactionIsolationLevel IsolationLevel { get; }

  /// <summary>
  /// True if the transaction cannot write.
  /// </summary>
  bool IsReadOnly { get; }

  /// <summary>
  /// The state of the transaction.
  /// </summary>
  TransactionState State { get; }

  /// <summary>
  /// Checks if the key exists in the snapshot of the transaction.
  /// </summary>
  bool ContainsKey(in TKey key);

  /// <summary>
  /// Gets the value of the key in the snapshot of the transaction.
  /// </summary>
  bool TryGet(in TKey key, out TValue value);

  /// <summary>
  /// Writes the value of the key.
  /// </summary>
  /// <exception cref="Exceptions.TransactionConflictException">
  /// A transaction committed a write to the key after this transaction began.
  /// The transaction is rolled back.</exception>
  void Upsert(in TKey key, in TValue value);

  /// <summary>
  /// Deletes the key.
  /// </summary>
  /// <exception cref="Exceptions.TransactionConflictException">
  /// A transaction committed a write to the key after this transaction began.
  /// The transaction is rolled back.</exception>
  void Delete(in TKey key);

  /// <summary>
  /// Creates an iterator over the snapshot of the transaction in ascending key order.
  /// In a serializable transaction, the iterated ranges join the read set.
  /// </summary>
  /// <remarks>
  /// The iterator sees the transaction's writes made before each Seek, SeekFirst or
  /// first Next call. It must be disposed, and it cannot be used after the transaction ends.
  /// </remarks>
  ITransactionIterator<TKey, TValue> CreateIterator(TransactionIteratorOptions options = null);

  /// <summary>
  /// Creates an iterator over the snapshot of the transaction in descending key order.
  /// </summary>
  /// <inheritdoc cref="CreateIterator"/>
  ITransactionIterator<TKey, TValue> CreateReverseIterator(TransactionIteratorOptions options = null);

  /// <summary>
  /// Commits the transaction. The commit is visible at once and becomes durable with
  /// the next write ahead log sync.
  /// </summary>
  /// <exception cref="Exceptions.TransactionConflictException">
  /// The transaction conflicts with a transaction that committed after it began.
  /// The transaction is rolled back.</exception>
  void Commit();

  /// <summary>
  /// Commits the transaction unless it conflicts.
  /// </summary>
  /// <returns>true if committed; false if the transaction conflicted and was rolled back.</returns>
  bool TryCommit();

  /// <summary>
  /// Commits the transaction and waits until the commit is durable.
  /// Concurrent callers share disk syncs.
  /// </summary>
  /// <param name="cancellationToken">Stops waiting for the sync; the commit itself is not undone.</param>
  /// <exception cref="Exceptions.TransactionConflictException">
  /// The transaction conflicts with a transaction that committed after it began.
  /// The transaction is rolled back.</exception>
  Task CommitDurableAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Rolls back the transaction. Does nothing if the transaction is not active.
  /// </summary>
  void Rollback();
}
