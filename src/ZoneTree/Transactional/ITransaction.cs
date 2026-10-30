namespace ZoneTree.Transactional;

/// <summary>
/// A transaction that reads and writes a transactional ZoneTree.
/// </summary>
/// <remarks>
/// A transaction reads the snapshot taken when it began, plus its own writes.
/// It is not thread-safe. Disposing an active transaction rolls it back.
/// Conflicts are reported by the commit, not by exceptions from reads or writes.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface ITransaction<TKey, TValue> : IReadTransaction<TKey, TValue>
{
  /// <summary>
  /// The transaction id.
  /// </summary>
  long TransactionId { get; }

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
  /// The state of the transaction.
  /// </summary>
  TransactionState State { get; }

  /// <summary>
  /// True once a write found its key committed by a transaction after this one began.
  /// The transaction stays active, but stores no more writes and its commit fails.
  /// </summary>
  /// <remarks>
  /// Writes report conflicts here instead of throwing. A long transaction can check it
  /// to stop early; the commit reports the conflict anyway.
  /// </remarks>
  bool HasConflict { get; }

  /// <summary>
  /// Gets the value of the key and protects it like a write: at every isolation level,
  /// the commit of a transaction that writes fails if a transaction committed the key
  /// after this one began.
  /// </summary>
  /// <remarks>
  /// Use it in a snapshot transaction for the keys a decision depends on, to prevent
  /// write skew without tracking every read. A transaction that writes nothing
  /// commits at its snapshot, so its reads need no protection.
  /// </remarks>
  bool TryGetForUpdate(in TKey key, out TValue value);

  /// <summary>
  /// Writes the value of the key. If a transaction committed the key after this one
  /// began, the write is not stored and <see cref="HasConflict"/> becomes true.
  /// </summary>
  void Upsert(in TKey key, in TValue value);

  /// <summary>
  /// Deletes the key. If a transaction committed the key after this one
  /// began, the delete is not stored and <see cref="HasConflict"/> becomes true.
  /// </summary>
  void Delete(in TKey key);

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
  /// Commits the transaction unless it conflicts, and waits until the commit is durable.
  /// Concurrent callers share disk syncs.
  /// </summary>
  /// <param name="cancellationToken">Stops waiting for the sync; the commit itself is not undone.</param>
  /// <returns>true if committed; false if the transaction conflicted and was rolled back.</returns>
  Task<bool> TryCommitDurableAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Rolls back the transaction. Does nothing if the transaction is not active.
  /// </summary>
  void Rollback();
}
