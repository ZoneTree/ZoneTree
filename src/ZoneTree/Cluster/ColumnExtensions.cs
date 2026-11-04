using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>
/// Single-column convenience operations, each using its own cluster transaction.
/// Use explicit cluster transactions to batch work or change several columns atomically.
/// </summary>
public static class ColumnExtensions
{
  /// <summary>Reads the key in a new cluster snapshot.</summary>
  public static bool TryGet<TKey, TValue>(
      this IColumn<TKey, TValue> column, in TKey key, out TValue value)
  {
    using var transaction = column.Cluster.BeginReadOnlyTransaction();
    return transaction.GetColumn(column).TryGet(in key, out value);
  }

  /// <summary>Checks the key in a new cluster snapshot.</summary>
  public static bool ContainsKey<TKey, TValue>(
      this IColumn<TKey, TValue> column, in TKey key)
  {
    using var transaction = column.Cluster.BeginReadOnlyTransaction();
    return transaction.GetColumn(column).ContainsKey(in key);
  }

  /// <summary>Writes the key in its own transaction, retrying on conflicts.</summary>
  public static void Upsert<TKey, TValue>(
      this IColumn<TKey, TValue> column, in TKey key, in TValue value)
  {
    var k = key;
    var v = value;
    column.Cluster.Execute(transaction => transaction.GetColumn(column).Upsert(k, v),
        TransactionIsolationLevel.Snapshot);
  }

  /// <summary>Deletes the key in its own transaction; returns whether it existed.</summary>
  public static bool TryDelete<TKey, TValue>(
      this IColumn<TKey, TValue> column, in TKey key)
  {
    var k = key;
    return column.Cluster.Execute(transaction =>
    {
      var view = transaction.GetColumn(column);
      if (!view.ContainsKey(k))
        return false;
      view.Delete(k);
      return true;
    }, TransactionIsolationLevel.Snapshot);
  }

  /// <summary>
  /// Adds or updates the key in its own transaction. Conflicts rerun the update
  /// delegate, which must not have external side effects. Returns the written value.
  /// </summary>
  public static TValue AddOrUpdate<TKey, TValue>(
      this IColumn<TKey, TValue> column,
      in TKey key,
      in TValue valueToAdd,
      Func<TValue, TValue> update)
  {
    ArgumentNullException.ThrowIfNull(update);
    var k = key;
    var added = valueToAdd;
    return column.Cluster.Execute(transaction =>
    {
      var view = transaction.GetColumn(column);
      var value = view.TryGet(k, out var current) ? update(current) : added;
      view.Upsert(k, value);
      return value;
    }, TransactionIsolationLevel.Snapshot);
  }

  /// <summary>Asynchronously reads the key in a new cluster snapshot.</summary>
  public static async ValueTask<ReadResult<TValue>> TryGetAsync<TKey, TValue>(
      this IColumn<TKey, TValue> column,
      TKey key,
      CancellationToken cancellationToken = default)
  {
    await using var transaction = await column.Cluster.BeginReadOnlyTransactionAsync(
        cancellationToken).ConfigureAwait(false);
    return await transaction.GetColumn(column).TryGetAsync(key, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Asynchronously checks the key in a new cluster snapshot.</summary>
  public static async ValueTask<bool> ContainsKeyAsync<TKey, TValue>(
      this IColumn<TKey, TValue> column,
      TKey key,
      CancellationToken cancellationToken = default)
  {
    await using var transaction = await column.Cluster.BeginReadOnlyTransactionAsync(
        cancellationToken).ConfigureAwait(false);
    return await transaction.GetColumn(column).ContainsKeyAsync(key, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Asynchronously writes and commits the key for process-crash durability,
  /// retrying only on conflicts. Use a cluster transaction for power-loss durability.
  /// </summary>
  public static ValueTask UpsertAsync<TKey, TValue>(
      this IColumn<TKey, TValue> column,
      TKey key,
      TValue value,
      CancellationToken cancellationToken = default)
      => column.Cluster.ExecuteAsync((transaction, token) =>
          transaction.GetColumn(column).UpsertAsync(key, value, token),
          TransactionIsolationLevel.Snapshot, cancellationToken: cancellationToken);

  /// <summary>Asynchronously deletes and commits the key; returns whether it existed.</summary>
  public static ValueTask<bool> TryDeleteAsync<TKey, TValue>(
      this IColumn<TKey, TValue> column,
      TKey key,
      CancellationToken cancellationToken = default)
      => column.Cluster.ExecuteAsync(async (transaction, token) =>
      {
        var view = transaction.GetColumn(column);
        if (!await view.ContainsKeyAsync(key, token).ConfigureAwait(false))
          return false;
        await view.DeleteAsync(key, token).ConfigureAwait(false);
        return true;
      }, TransactionIsolationLevel.Snapshot, cancellationToken: cancellationToken);

  /// <summary>
  /// Asynchronously adds or updates and commits the key. Conflicts rerun the synchronous
  /// update delegate, which must have no external side effects. Returns the written value.
  /// </summary>
  public static ValueTask<TValue> AddOrUpdateAsync<TKey, TValue>(
      this IColumn<TKey, TValue> column,
      TKey key,
      TValue valueToAdd,
      Func<TValue, TValue> update,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(update);
    return column.Cluster.ExecuteAsync(async (transaction, token) =>
    {
      var view = transaction.GetColumn(column);
      var current = await view.TryGetAsync(key, token).ConfigureAwait(false);
      var value = current.Found ? update(current.Value) : valueToAdd;
      await view.UpsertAsync(key, value, token).ConfigureAwait(false);
      return value;
    }, TransactionIsolationLevel.Snapshot, cancellationToken: cancellationToken);
  }
}
