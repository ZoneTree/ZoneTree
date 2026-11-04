using ZoneTree.Exceptions;

namespace ZoneTree.Transactional.Distributed;

/// <summary>Optional transaction helpers with the same retry rules as single-tree helpers.</summary>
public static class DistributedDatabaseExtensions
{
  /// <summary>
  /// Runs a database transaction and retries its entire body only on a conflict.
  /// The body may run more than once; do not perform external side effects or end
  /// the transaction in it. Storage failures and uncertain outcomes are never retried.
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
      await using var transaction = await database.BeginTransactionAsync(
          isolationLevel, cancellationToken).ConfigureAwait(false);
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

  /// <summary>
  /// Runs an asynchronous body and commits for process-crash durability, retrying
  /// only on conflicts. The body must await all issued operations, including on
  /// failure, and must not end the transaction or perform external side effects.
  /// </summary>
  public static ValueTask<TResult> ExecuteAsync<TResult>(
      this IDistributedDatabase database,
      Func<IDistributedTransaction, CancellationToken, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      => ExecuteAsyncCore(database, body, false, isolationLevel, maxAttempts, cancellationToken);

  /// <summary>Runs an asynchronous body without a token parameter and commits for process-crash durability.</summary>
  public static ValueTask<TResult> ExecuteAsync<TResult>(
      this IDistributedDatabase database,
      Func<IDistributedTransaction, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    return database.ExecuteAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  /// <summary>Runs an asynchronous body and commits for process-crash durability.</summary>
  public static async ValueTask ExecuteAsync(
      this IDistributedDatabase database,
      Func<IDistributedTransaction, CancellationToken, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    await database.ExecuteAsync(async (transaction, token) =>
    {
      await body(transaction, token).ConfigureAwait(false);
      return true;
    }, isolationLevel, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Runs an asynchronous body without a token parameter and commits for process-crash durability.</summary>
  public static ValueTask ExecuteAsync(
      this IDistributedDatabase database,
      Func<IDistributedTransaction, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    return database.ExecuteAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  /// <summary>
  /// Runs an asynchronous body and commits for power-loss durability, retrying only
  /// on conflicts. The body must await all issued operations, including on failure.
  /// Cancellation and storage failures propagate without retrying the transaction.
  /// </summary>
  public static ValueTask<TResult> ExecuteDurableAsync<TResult>(
      this IDistributedDatabase database,
      Func<IDistributedTransaction, CancellationToken, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      => ExecuteAsyncCore(database, body, true, isolationLevel, maxAttempts, cancellationToken);

  /// <summary>Awaits an asynchronous body without a token parameter before a power-loss durable commit.</summary>
  public static ValueTask<TResult> ExecuteDurableAsync<TResult>(
      this IDistributedDatabase database,
      Func<IDistributedTransaction, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    return database.ExecuteDurableAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  /// <summary>Runs an asynchronous body and commits for power-loss durability.</summary>
  public static async ValueTask ExecuteDurableAsync(
      this IDistributedDatabase database,
      Func<IDistributedTransaction, CancellationToken, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    await database.ExecuteDurableAsync(async (transaction, token) =>
    {
      await body(transaction, token).ConfigureAwait(false);
      return true;
    }, isolationLevel, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Awaits an asynchronous body without a token parameter before a power-loss durable commit.</summary>
  public static ValueTask ExecuteDurableAsync(
      this IDistributedDatabase database,
      Func<IDistributedTransaction, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    return database.ExecuteDurableAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  static async ValueTask<TResult> ExecuteAsyncCore<TResult>(
      IDistributedDatabase database,
      Func<IDistributedTransaction, CancellationToken, ValueTask<TResult>> body,
      bool durable,
      TransactionIsolationLevel isolationLevel,
      int maxAttempts,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(database);
    ArgumentNullException.ThrowIfNull(body);
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      cancellationToken.ThrowIfCancellationRequested();
      await using var transaction = await database.BeginTransactionAsync(
          isolationLevel, cancellationToken).ConfigureAwait(false);
      var result = await body(transaction, cancellationToken).ConfigureAwait(false);
      if (transaction.State != TransactionState.Active)
        throw new InvalidOperationException("The transaction body ended the transaction. The helper commits it.");
      var committed = durable
          ? await transaction.TryCommitDurableAsync(cancellationToken).ConfigureAwait(false)
          : await transaction.TryCommitAsync(cancellationToken).ConfigureAwait(false);
      if (committed)
        return result;
      if (attempt == maxAttempts)
        throw new TransactionConflictException(transaction.TransactionId);
    }
  }
}
