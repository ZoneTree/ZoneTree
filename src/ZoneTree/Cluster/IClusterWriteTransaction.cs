using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>
/// The state, commit and rollback of a writing transaction, for domain and standalone transactions.
/// </summary>
/// <remarks>
/// Disposing an active transaction rolls it back. In a remote cluster the synchronous
/// methods block the calling thread for a network round trip; prefer the asynchronous ones.
/// </remarks>
public interface IClusterWriteTransaction : IDisposable, IAsyncDisposable
{
  /// <summary>The transaction id.</summary>
  long TransactionId { get; }

  /// <summary>The commit timestamp, or zero until committed.</summary>
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
  /// Atomically commits the transaction with process-crash durability.
  /// </summary>
  /// <exception cref="Exceptions.TransactionConflictException">
  /// Validation failed. The transaction is rolled back.</exception>
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
  /// Validation failed. The transaction is rolled back.</exception>
  Task CommitDurableAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Attempts a power-loss and OS-crash durable commit. Returns false on a conflict.
  /// </summary>
  Task<bool> TryCommitDurableAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Rolls back the active transaction across all of its columns.
  /// </summary>
  void Rollback();

  /// <summary>
  /// Asynchronously rolls back the active transaction across all of its columns.
  /// </summary>
  ValueTask RollbackAsync();
}
