using ZoneTree.Exceptions;
using ZoneTree.Transactional;

namespace ZoneTree;

/// <summary>
/// Runs transactions on a <see cref="ITransactionalZoneTree{TKey, TValue}"/>
/// with conflict retries, and offers single-key operations.
/// </summary>
public static class TransactionalZoneTreeExtensions
{
  /// <summary>
  /// Runs a transaction and commits it, retrying from the start on a conflict.
  /// </summary>
  /// <remarks>
  /// The body may run more than once, so it must not have effects outside the
  /// transaction, and it must not end the transaction. To abandon the work,
  /// throw: the transaction is rolled back and the exception propagates.
  /// A long body can check <see cref="ITransaction{TKey, TValue}.HasConflict"/>
  /// to stop early. Every conflict means another transaction committed, so the
  /// tree as a whole always makes progress.
  /// </remarks>
  /// <param name="tree">The tree.</param>
  /// <param name="body">Reads and writes through the transaction.</param>
  /// <param name="isolationLevel">The isolation level.</param>
  /// <param name="maxAttempts">The attempts before the conflict is thrown.</param>
  /// <returns>The result of the body in the committed attempt.</returns>
  /// <exception cref="TransactionConflictException">The last attempt conflicted.</exception>
  /// <exception cref="InvalidOperationException">The body ended the transaction.</exception>
  public static TResult Execute<TKey, TValue, TResult>(
      this ITransactionalZoneTree<TKey, TValue> tree,
      Func<ITransaction<TKey, TValue>, TResult> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue)
  {
    ArgumentNullException.ThrowIfNull(body);
    if (maxAttempts < 1)
      throw new ArgumentOutOfRangeException(
          nameof(maxAttempts), maxAttempts, "A transaction needs at least one attempt.");
    for (var attempt = 1; ; ++attempt)
    {
      using var transaction = tree.BeginTransaction(isolationLevel);
      var result = body(transaction);
      if (transaction.State != TransactionState.Active)
        throw new InvalidOperationException(
            "The transaction body ended the transaction. Execute commits it.");
      if (transaction.TryCommit())
        return result;
      if (attempt == maxAttempts)
        throw new TransactionConflictException(transaction.TransactionId);
    }
  }

  /// <inheritdoc cref="Execute{TKey, TValue, TResult}"/>
  public static void Execute<TKey, TValue>(
      this ITransactionalZoneTree<TKey, TValue> tree,
      Action<ITransaction<TKey, TValue>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue)
  {
    ArgumentNullException.ThrowIfNull(body);
    tree.Execute(transaction =>
    {
      body(transaction);
      return true;
    }, isolationLevel, maxAttempts);
  }

  /// <summary>
  /// Runs a transaction like <see cref="Execute{TKey, TValue, TResult}"/>
  /// and waits until its commit is durable.
  /// </summary>
  /// <inheritdoc cref="Execute{TKey, TValue, TResult}"/>
  /// <param name="cancellationToken">Stops waiting for the sync; the commit itself is not undone.</param>
  public static async Task<TResult> ExecuteDurableAsync<TKey, TValue, TResult>(
      this ITransactionalZoneTree<TKey, TValue> tree,
      Func<ITransaction<TKey, TValue>, TResult> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    var result = tree.Execute(body, isolationLevel, maxAttempts);
    await tree.SyncWALAsync(cancellationToken).ConfigureAwait(false);
    return result;
  }

  /// <inheritdoc cref="ExecuteDurableAsync{TKey, TValue, TResult}"/>
  public static async Task ExecuteDurableAsync<TKey, TValue>(
      this ITransactionalZoneTree<TKey, TValue> tree,
      Action<ITransaction<TKey, TValue>> body,
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      int maxAttempts = int.MaxValue,
      CancellationToken cancellationToken = default)
  {
    tree.Execute(body, isolationLevel, maxAttempts);
    await tree.SyncWALAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Gets the value of the key in the latest snapshot.
  /// </summary>
  public static bool TryGet<TKey, TValue>(
      this ITransactionalZoneTree<TKey, TValue> tree, in TKey key, out TValue value)
  {
    using var transaction = tree.BeginReadOnlyTransaction();
    return transaction.TryGet(in key, out value);
  }

  /// <summary>
  /// Checks if the key exists in the latest snapshot.
  /// </summary>
  public static bool ContainsKey<TKey, TValue>(
      this ITransactionalZoneTree<TKey, TValue> tree, in TKey key)
  {
    using var transaction = tree.BeginReadOnlyTransaction();
    return transaction.ContainsKey(in key);
  }

  /// <summary>
  /// Writes the value of the key in its own transaction.
  /// A concurrent commit to the key makes it retry; the last commit wins.
  /// </summary>
  public static void Upsert<TKey, TValue>(
      this ITransactionalZoneTree<TKey, TValue> tree, in TKey key, in TValue value)
  {
    var k = key;
    var v = value;
    tree.Execute(transaction => transaction.Upsert(k, v), TransactionIsolationLevel.Snapshot);
  }

  /// <summary>
  /// Deletes the key in its own transaction.
  /// </summary>
  /// <returns>true if the key existed.</returns>
  public static bool TryDelete<TKey, TValue>(
      this ITransactionalZoneTree<TKey, TValue> tree, in TKey key)
  {
    var k = key;
    return tree.Execute(transaction =>
    {
      // Deleting the key protects it from concurrent commits,
      // and a missing key is read in one snapshot.
      if (!transaction.ContainsKey(k))
        return false;
      transaction.Delete(k);
      return true;
    }, TransactionIsolationLevel.Snapshot);
  }

  /// <summary>
  /// Adds the key or updates its value in its own transaction.
  /// A concurrent commit to the key makes it retry, so no update is lost.
  /// </summary>
  /// <param name="tree">The tree.</param>
  /// <param name="key">The key.</param>
  /// <param name="valueToAdd">The value of a missing key.</param>
  /// <param name="update">Computes the new value from the current one.
  /// It may run more than once.</param>
  /// <returns>The value written.</returns>
  public static TValue AddOrUpdate<TKey, TValue>(
      this ITransactionalZoneTree<TKey, TValue> tree,
      in TKey key,
      in TValue valueToAdd,
      Func<TValue, TValue> update)
  {
    ArgumentNullException.ThrowIfNull(update);
    var k = key;
    var added = valueToAdd;
    return tree.Execute(transaction =>
    {
      // Writing the key protects the value read from concurrent commits.
      var value = transaction.TryGet(k, out var current) ? update(current) : added;
      transaction.Upsert(k, value);
      return value;
    }, TransactionIsolationLevel.Snapshot);
  }
}
