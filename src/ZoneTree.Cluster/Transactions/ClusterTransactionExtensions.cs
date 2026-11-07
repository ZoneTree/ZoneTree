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
  /// Each attempt begins a transaction with the options, so a timeout applies to each
  /// attempt; an attempt that times out throws and is not retried.
  /// </summary>
  /// <exception cref="ArgumentException">The body returns a task. Use ExecuteAsync.</exception>
  public static TResult Execute<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, TResult> body,
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(body);
    RejectAsynchronousBody<TResult>();
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      using var transaction = source.BeginTransaction(options);
      var result = body(transaction);
      // An aborted transaction, rolled back by the body or by its timeout, fails its commit.
      if (transaction.State is not (TransactionState.Active or TransactionState.Aborted))
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
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    source.Execute(transaction =>
    {
      body(transaction);
      return true;
    }, options, maxAttempts);
  }

  /// <summary>
  /// Runs and durably commits a cluster transaction, retrying only on a conflict.
  /// Cancellation stops waiting for a started commit; the commit may still succeed.
  /// The body must not perform external side effects or end the transaction.
  /// </summary>
  /// <exception cref="ArgumentException">
  /// The body returns a task. Pass an asynchronous body, which is awaited before the commit.
  /// </exception>
  public static async Task<TResult> ExecuteDurableAsync<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, TResult> body,
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(body);
    RejectAsynchronousBody<TResult>();
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      cancellationToken.ThrowIfCancellationRequested();
      await using var transaction = await source.BeginTransactionAsync(
          options, cancellationToken).ConfigureAwait(false);
      var result = body(transaction);
      if (transaction.State is not (TransactionState.Active or TransactionState.Aborted))
        throw new InvalidOperationException("The transaction body ended the transaction. ExecuteDurableAsync commits it.");
      if (await transaction.TryCommitDurableAsync(cancellationToken).ConfigureAwait(false))
        return result;
      if (attempt == maxAttempts)
        throw new TransactionConflictException(transaction.TransactionId);
    }
  }

  /// <summary>Runs a body and durably commits it, retrying only on a conflict.</summary>
  public static async Task ExecuteDurableAsync<TTransaction>(
      this IClusterTransactionSource<TTransaction> source,
      Action<TTransaction> body,
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    await source.ExecuteDurableAsync(transaction =>
    {
      body(transaction);
      return true;
    }, options, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Runs an asynchronous body and commits for process-crash durability, retrying
  /// only on conflicts. The body must await all issued operations, including on
  /// failure, and must not end the transaction or perform external side effects.
  /// </summary>
  public static Task<TResult> ExecuteAsync<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, CancellationToken, Task<TResult>> body,
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
      => ExecuteAsyncCore(source, body, false, options, maxAttempts, cancellationToken);

  /// <summary>Runs an asynchronous body without a token parameter and commits for process-crash durability.</summary>
  public static Task<TResult> ExecuteAsync<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, Task<TResult>> body,
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    return source.ExecuteAsync((transaction, _) => body(transaction),
        options, maxAttempts, cancellationToken);
  }

  /// <summary>Runs an asynchronous body and commits for process-crash durability.</summary>
  public static async Task ExecuteAsync<TTransaction>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, CancellationToken, Task> body,
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    await source.ExecuteAsync(async (transaction, token) =>
    {
      await body(transaction, token).ConfigureAwait(false);
      return true;
    }, options, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Runs an asynchronous body without a token parameter and commits for process-crash durability.</summary>
  public static Task ExecuteAsync<TTransaction>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, Task> body,
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    return source.ExecuteAsync((transaction, _) => body(transaction),
        options, maxAttempts, cancellationToken);
  }

  /// <summary>
  /// Runs an asynchronous body and commits for power-loss durability, retrying only
  /// on conflicts. The body must await all issued operations, including on failure.
  /// Cancellation and storage failures propagate without retrying the transaction.
  /// </summary>
  public static Task<TResult> ExecuteDurableAsync<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, CancellationToken, Task<TResult>> body,
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
      => ExecuteAsyncCore(source, body, true, options, maxAttempts, cancellationToken);

  /// <summary>Awaits an asynchronous body without a token parameter before a power-loss durable commit.</summary>
  public static Task<TResult> ExecuteDurableAsync<TTransaction, TResult>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, Task<TResult>> body,
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    return source.ExecuteDurableAsync((transaction, _) => body(transaction),
        options, maxAttempts, cancellationToken);
  }

  /// <summary>Runs an asynchronous body and commits for power-loss durability.</summary>
  public static async Task ExecuteDurableAsync<TTransaction>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, CancellationToken, Task> body,
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    await source.ExecuteDurableAsync(async (transaction, token) =>
    {
      await body(transaction, token).ConfigureAwait(false);
      return true;
    }, options, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Awaits an asynchronous body without a token parameter before a power-loss durable commit.</summary>
  public static Task ExecuteDurableAsync<TTransaction>(
      this IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, Task> body,
      ClusterTransactionOptions options = null,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      where TTransaction : IClusterWriteTransaction
  {
    ArgumentNullException.ThrowIfNull(body);
    return source.ExecuteDurableAsync((transaction, _) => body(transaction),
        options, maxAttempts, cancellationToken);
  }

  /// <summary>
  /// Rejects a body that returns a task: a synchronous helper would commit before the
  /// task's operations run, so the transaction would commit without them.
  /// </summary>
  static void RejectAsynchronousBody<TResult>()
  {
    var type = typeof(TResult);
    if (typeof(Task).IsAssignableFrom(type) || type == typeof(Task) ||
        (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)))
      throw new ArgumentException(
          "The transaction body returns a task, which would run after the commit. " +
          "Use ExecuteAsync, or ExecuteDurableAsync with an asynchronous body, which await it before committing.",
          "body");
  }

  static async Task<TResult> ExecuteAsyncCore<TTransaction, TResult>(
      IClusterTransactionSource<TTransaction> source,
      Func<TTransaction, CancellationToken, Task<TResult>> body,
      bool durable,
      ClusterTransactionOptions options,
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
          options, cancellationToken).ConfigureAwait(false);
      var result = await body(transaction, cancellationToken).ConfigureAwait(false);
      if (transaction.State is not (TransactionState.Active or TransactionState.Aborted))
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
