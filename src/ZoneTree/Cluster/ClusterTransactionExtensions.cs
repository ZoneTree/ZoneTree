using ZoneTree.Transactional;
using ZoneTree.Exceptions;

namespace ZoneTree.Cluster;

/// <summary>Transaction helpers for domains and standalone columns, with the same retry rules as single-tree helpers.</summary>
public static class ClusterTransactionExtensions
{
  /// <summary>
  /// Runs a cluster transaction and retries its entire body only on a conflict.
  /// The body may run more than once; do not perform external side effects or end
  /// the transaction in it. Storage failures and uncertain outcomes are never retried.
  /// </summary>
  public static TResult Execute<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, TResult> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(body);
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      using var transaction = source.BeginTransaction(isolationLevel);
      var result = body(transaction);
      if (transaction.State != TransactionState.Active)
        throw new InvalidOperationException("The transaction body ended the transaction. Execute commits it.");
      if (transaction.TryCommit())
        return result;
      if (attempt == maxAttempts)
        throw new TransactionConflictException(transaction.TransactionId);
    }
  }

  /// <summary>Runs a cluster transaction, retrying the entire body only on a conflict.</summary>
  public static void Execute<TTransaction>(
      this IClusterTransactionSource<TTransaction> source,
      Action<TTransaction> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    source.Execute(transaction =>
    {
      body(transaction);
      return true;
    }, isolationLevel, maxAttempts);
  }

  /// <summary>
  /// Runs and durably commits a cluster transaction, retrying only on a conflict.
  /// Cancellation stops waiting for a started commit; the commit may still succeed.
  /// The body must not perform external side effects or end the transaction.
  /// </summary>
  public static async Task<TResult> ExecuteDurableAsync<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, TResult> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(body);
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      cancellationToken.ThrowIfCancellationRequested();
      await using var transaction = await source.BeginTransactionAsync(
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
  public static Task ExecuteDurableAsync<TTransaction>(
      this IClusterTransactionSource<TTransaction> source,
      Action<TTransaction> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    return source.ExecuteDurableAsync(transaction =>
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
  public static ValueTask<TResult> ExecuteAsync<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, CancellationToken, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
      => ExecuteAsyncCore(source, body, false, isolationLevel, maxAttempts, cancellationToken);

  /// <summary>Runs an asynchronous body without a token parameter and commits for process-crash durability.</summary>
  public static ValueTask<TResult> ExecuteAsync<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    return source.ExecuteAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  /// <summary>Runs an asynchronous body and commits for process-crash durability.</summary>
  public static async ValueTask ExecuteAsync<TTransaction>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, CancellationToken, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    await source.ExecuteAsync(async (transaction, token) =>
    {
      await body(transaction, token).ConfigureAwait(false);
      return true;
    }, isolationLevel, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Runs an asynchronous body without a token parameter and commits for process-crash durability.</summary>
  public static ValueTask ExecuteAsync<TTransaction>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    return source.ExecuteAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  /// <summary>
  /// Runs an asynchronous body and commits for power-loss durability, retrying only
  /// on conflicts. The body must await all issued operations, including on failure.
  /// Cancellation and storage failures propagate without retrying the transaction.
  /// </summary>
  public static ValueTask<TResult> ExecuteDurableAsync<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, CancellationToken, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
      => ExecuteAsyncCore(source, body, true, isolationLevel, maxAttempts, cancellationToken);

  /// <summary>Awaits an asynchronous body without a token parameter before a power-loss durable commit.</summary>
  public static ValueTask<TResult> ExecuteDurableAsync<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    return source.ExecuteDurableAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  /// <summary>Runs an asynchronous body and commits for power-loss durability.</summary>
  public static async ValueTask ExecuteDurableAsync<TTransaction>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, CancellationToken, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    await source.ExecuteDurableAsync(async (transaction, token) =>
    {
      await body(transaction, token).ConfigureAwait(false);
      return true;
    }, isolationLevel, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Awaits an asynchronous body without a token parameter before a power-loss durable commit.</summary>
  public static ValueTask ExecuteDurableAsync<TTransaction>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    return source.ExecuteDurableAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  static async ValueTask<TResult> ExecuteAsyncCore<TTransaction, TResult>(
      IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, CancellationToken, ValueTask<TResult>> body,
      bool durable,
      TransactionIsolationLevel isolationLevel,
      int maxAttempts,
      CancellationToken cancellationToken)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(body);
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      cancellationToken.ThrowIfCancellationRequested();
      await using var transaction = await source.BeginTransactionAsync(
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
