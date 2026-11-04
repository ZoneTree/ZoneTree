namespace ZoneTree.Transactional.Distributed;

/// <summary>An atomic transaction over any number of independently typed columns.</summary>
/// <remarks>
/// Writes enter the owning column's MVCC storage as they are made; values need not
/// be buffered in a database-wide write set. Other transactions cannot see them
/// until the database publishes one commit decision. Disposing an active transaction
/// rolls it back across all written columns. Column views cannot commit independently.
/// A transaction that wrote nothing commits at its snapshot without a durability sync.
/// </remarks>
public interface IDistributedTransaction : IDistributedReadTransaction
{
  /// <summary>The transaction id, unique across this database and its reopenings.</summary>
  long TransactionId { get; }

  /// <summary>The database commit timestamp, or zero until committed.</summary>
  long CommitTimestamp { get; }

  /// <summary>The isolation level, applied across all accessed columns.</summary>
  TransactionIsolationLevel IsolationLevel { get; }

  /// <summary>The state of the whole transaction.</summary>
  TransactionState State { get; }

  /// <summary>
  /// True once any column detects a conflict. Subsequent writes in every column
  /// are ignored and commit rolls back the entire transaction. A conflict discovered
  /// only during commit is also reported by TryCommit or TryCommitDurableAsync.
  /// </summary>
  bool HasConflict { get; }

  /// <summary>
  /// Binds a writable column view to this transaction. Repeated calls through
  /// either transaction interface return the same view. Views share this transaction's
  /// snapshot, conflict status and outcome.
  /// </summary>
  new IDistributedWriteColumn<TKey, TValue> GetColumn<TKey, TValue>(
      IDistributedColumn<TKey, TValue> column);

  /// <summary>
  /// Commits all written columns atomically and publishes them together.
  /// Success survives a process crash: all required data and the commit decision
  /// have reached the OS. It does not wait for a disk sync for power-loss durability.
  /// An I/O failure after the decision may leave State InDoubt; it is not a conflict.
  /// </summary>
  /// <exception cref="Exceptions.TransactionConflictException">
  /// Validation failed. Every written column is rolled back.</exception>
  void Commit();

  /// <summary>
  /// Commits with the same guarantee as Commit. Returns false only for a conflict,
  /// after rolling back all written columns. Storage failures propagate as exceptions.
  /// </summary>
  bool TryCommit();

  /// <summary>
  /// Makes the data, required recovery dependencies and commit decision durable
  /// before publishing any of this transaction's writes. Success survives a power
  /// loss or OS crash. Concurrent commits may share syncs without weakening ordering.
  /// </summary>
  /// <param name="cancellationToken">
  /// Cancels before starting, or stops waiting once commit starts. A started commit
  /// continues; cancellation does not mean rollback and is not safe to retry blindly.
  /// </param>
  /// <remarks>
  /// A failure with an uncertain decision leaves State InDoubt and writes invisible
  /// in this instance; recovery determines the whole transaction's outcome.
  /// </remarks>
  /// <exception cref="Exceptions.TransactionConflictException">
  /// Validation failed. Every written column is rolled back.</exception>
  Task CommitDurableAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Uses the same durability and cancellation rules as CommitDurableAsync.
  /// Returns false only for a conflict, after rolling back all written columns.
  /// A storage failure or uncertain outcome is an exception, not a false result.
  /// </summary>
  Task<bool> TryCommitDurableAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Rolls back all written columns and ends the transaction. Does nothing once
  /// it is no longer Active; a Committing or InDoubt transaction cannot be rolled back.
  /// </summary>
  void Rollback();
}
