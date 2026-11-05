using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>An atomic transaction over any number of independently typed columns.</summary>
/// <remarks>
/// Commit and rollback apply to all columns accessed by this transaction.
/// Disposing an active transaction rolls it back.
/// </remarks>
public interface IClusterTransaction : IClusterReadTransaction
{
  /// <summary>The transaction id.</summary>
  long TransactionId { get; }

  /// <summary>The cluster commit timestamp, or zero until committed.</summary>
  long CommitTimestamp { get; }

  /// <summary>The isolation level, applied across all accessed columns.</summary>
  TransactionIsolationLevel IsolationLevel { get; }

  /// <summary>The state of the whole transaction.</summary>
  TransactionState State { get; }

  /// <summary>
  /// Whether a transaction conflict has been detected.
  /// </summary>
  bool HasConflict { get; }

  /// <summary>
  /// Gets a writable column view within this transaction.
  /// </summary>
  new IWriteColumn<TKey, TValue> GetColumn<TKey, TValue>(
      ICoordinatedColumn<TKey, TValue> column);

  /// <summary>
  /// Atomically commits the transaction with process-crash durability.
  /// </summary>
  /// <exception cref="Exceptions.TransactionConflictException">
  /// Validation failed. Every written column is rolled back.</exception>
  void Commit();

  /// <summary>
  /// Attempts a process-crash durable commit. Returns false on a transaction conflict.
  /// </summary>
  bool TryCommit();

  /// <summary>
  /// Asynchronously commits with the same process-crash durability as Commit.
  /// </summary>
  ValueTask CommitAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Asynchronously attempts a process-crash durable commit. Returns false on a conflict.
  /// </summary>
  ValueTask<bool> TryCommitAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Atomically commits the transaction with power-loss and OS-crash durability.
  /// </summary>
  /// <exception cref="Exceptions.TransactionConflictException">
  /// Validation failed. Every written column is rolled back.</exception>
  Task CommitDurableAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Attempts a power-loss and OS-crash durable commit. Returns false on a conflict.
  /// </summary>
  Task<bool> TryCommitDurableAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Rolls back the active transaction across all columns.
  /// </summary>
  void Rollback();

  /// <summary>
  /// Asynchronously rolls back the active transaction across all columns.
  /// </summary>
  ValueTask RollbackAsync();
}
