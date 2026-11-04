using ZoneTree.Exceptions;

namespace ZoneTree.Transactional.Distributed;

/// <summary>Optional transaction helpers with the same retry rules as single-tree helpers.</summary>
public static class DistributedDatabaseExtensions
{
  /// <summary>
  /// Runs a database transaction and retries its entire body only on a conflict.
  /// The body may run more than once; do not perform external side effects or end
  /// the transaction in it. Storage failures and uncertain outcomes are never retried.
  /// Direct transaction access avoids these convenience delegates on hot paths.
  /// </summary>
  public static TResult Execute<TResult>(
      this IDistributedDatabase database,
      Func<IDistributedTransaction, TResult> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue)
  {
    ArgumentNullException.ThrowIfNull(database);
    ArgumentNullException.ThrowIfNull(body);
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      using var transaction = database.BeginTransaction(isolationLevel);
      var result = body(transaction);
      if (transaction.State != TransactionState.Active)
        throw new InvalidOperationException("The transaction body ended the transaction. Execute commits it.");
      if (transaction.TryCommit())
        return result;
      if (attempt == maxAttempts)
        throw new TransactionConflictException(transaction.TransactionId);
    }
  }

  /// <summary>Runs a database transaction, retrying the entire body only on a conflict.</summary>
  public static void Execute(
      this IDistributedDatabase database,
      Action<IDistributedTransaction> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue)
  {
    ArgumentNullException.ThrowIfNull(body);
    database.Execute(transaction =>
    {
      body(transaction);
      return true;
    }, isolationLevel, maxAttempts);
  }

  /// <summary>
  /// Runs and durably commits a database transaction, retrying only on a conflict.
  /// Cancellation stops waiting for a started commit; the commit may still succeed.
  /// The body must not perform external side effects or end the transaction.
  /// </summary>
  public static async Task<TResult> ExecuteDurableAsync<TResult>(
      this IDistributedDatabase database,
      Func<IDistributedTransaction, TResult> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(database);
    ArgumentNullException.ThrowIfNull(body);
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      cancellationToken.ThrowIfCancellationRequested();
      using var transaction = database.BeginTransaction(isolationLevel);
      var result = body(transaction);
      if (transaction.State != TransactionState.Active)
        throw new InvalidOperationException("The transaction body ended the transaction. ExecuteDurableAsync commits it.");
      if (await transaction.TryCommitDurableAsync(cancellationToken).ConfigureAwait(false))
        return result;
      if (attempt == maxAttempts)
        throw new TransactionConflictException(transaction.TransactionId);
    }
  }

  /// <summary>Runs a body and durably commits it, retrying only on a conflict.</summary>
  public static Task ExecuteDurableAsync(
      this IDistributedDatabase database,
      Action<IDistributedTransaction> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    return database.ExecuteDurableAsync(transaction =>
    {
      body(transaction);
      return true;
    }, isolationLevel, maxAttempts, cancellationToken);
  }
}
