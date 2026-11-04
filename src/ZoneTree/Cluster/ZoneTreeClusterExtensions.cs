using ZoneTree.Transactional;
using ZoneTree.Exceptions;

namespace ZoneTree.Cluster;

/// <summary>Optional transaction helpers with the same retry rules as single-tree helpers.</summary>
public static class ZoneTreeClusterExtensions
{
  /// <summary>
  /// Runs a cluster transaction and retries its entire body only on a conflict.
  /// The body may run more than once; do not perform external side effects or end
  /// the transaction in it. Storage failures and uncertain outcomes are never retried.
  /// </summary>
  public static TResult Execute<TResult>(
      this IZoneTreeCluster cluster,
      Func<IClusterTransaction, TResult> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue)
  {
    ArgumentNullException.ThrowIfNull(cluster);
    ArgumentNullException.ThrowIfNull(body);
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      using var transaction = cluster.BeginTransaction(isolationLevel);
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
  public static void Execute(
      this IZoneTreeCluster cluster,
      Action<IClusterTransaction> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue)
  {
    ArgumentNullException.ThrowIfNull(body);
    cluster.Execute(transaction =>
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
  public static async Task<TResult> ExecuteDurableAsync<TResult>(
      this IZoneTreeCluster cluster,
      Func<IClusterTransaction, TResult> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(cluster);
    ArgumentNullException.ThrowIfNull(body);
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      cancellationToken.ThrowIfCancellationRequested();
      await using var transaction = await cluster.BeginTransactionAsync(
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
      this IZoneTreeCluster cluster,
      Action<IClusterTransaction> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    return cluster.ExecuteDurableAsync(transaction =>
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
      this IZoneTreeCluster cluster,
      Func<IClusterTransaction, CancellationToken, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      => ExecuteAsyncCore(cluster, body, false, isolationLevel, maxAttempts, cancellationToken);

  /// <summary>Runs an asynchronous body without a token parameter and commits for process-crash durability.</summary>
  public static ValueTask<TResult> ExecuteAsync<TResult>(
      this IZoneTreeCluster cluster,
      Func<IClusterTransaction, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    return cluster.ExecuteAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  /// <summary>Runs an asynchronous body and commits for process-crash durability.</summary>
  public static async ValueTask ExecuteAsync(
      this IZoneTreeCluster cluster,
      Func<IClusterTransaction, CancellationToken, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    await cluster.ExecuteAsync(async (transaction, token) =>
    {
      await body(transaction, token).ConfigureAwait(false);
      return true;
    }, isolationLevel, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Runs an asynchronous body without a token parameter and commits for process-crash durability.</summary>
  public static ValueTask ExecuteAsync(
      this IZoneTreeCluster cluster,
      Func<IClusterTransaction, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    return cluster.ExecuteAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  /// <summary>
  /// Runs an asynchronous body and commits for power-loss durability, retrying only
  /// on conflicts. The body must await all issued operations, including on failure.
  /// Cancellation and storage failures propagate without retrying the transaction.
  /// </summary>
  public static ValueTask<TResult> ExecuteDurableAsync<TResult>(
      this IZoneTreeCluster cluster,
      Func<IClusterTransaction, CancellationToken, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
      => ExecuteAsyncCore(cluster, body, true, isolationLevel, maxAttempts, cancellationToken);

  /// <summary>Awaits an asynchronous body without a token parameter before a power-loss durable commit.</summary>
  public static ValueTask<TResult> ExecuteDurableAsync<TResult>(
      this IZoneTreeCluster cluster,
      Func<IClusterTransaction, ValueTask<TResult>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    return cluster.ExecuteDurableAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  /// <summary>Runs an asynchronous body and commits for power-loss durability.</summary>
  public static async ValueTask ExecuteDurableAsync(
      this IZoneTreeCluster cluster,
      Func<IClusterTransaction, CancellationToken, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    await cluster.ExecuteDurableAsync(async (transaction, token) =>
    {
      await body(transaction, token).ConfigureAwait(false);
      return true;
    }, isolationLevel, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Awaits an asynchronous body without a token parameter before a power-loss durable commit.</summary>
  public static ValueTask ExecuteDurableAsync(
      this IZoneTreeCluster cluster,
      Func<IClusterTransaction, ValueTask> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(body);
    return cluster.ExecuteDurableAsync((transaction, _) => body(transaction),
        isolationLevel, maxAttempts, cancellationToken);
  }

  static async ValueTask<TResult> ExecuteAsyncCore<TResult>(
      IZoneTreeCluster cluster,
      Func<IClusterTransaction, CancellationToken, ValueTask<TResult>> body,
      bool durable,
      TransactionIsolationLevel isolationLevel,
      int maxAttempts,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(cluster);
    ArgumentNullException.ThrowIfNull(body);
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      cancellationToken.ThrowIfCancellationRequested();
      await using var transaction = await cluster.BeginTransactionAsync(
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
