namespace ZoneTree.Transactional.Distributed;

/// <summary>
/// Single-column convenience operations, each using its own database transaction.
/// Use explicit database transactions to batch work or change several columns atomically.
/// </summary>
public static class DistributedColumnExtensions
{
  /// <summary>Reads the key in a new database snapshot.</summary>
  public static bool TryGet<TKey, TValue>(
      this IDistributedColumn<TKey, TValue> column, in TKey key, out TValue value)
  {
    using var transaction = column.Database.BeginReadOnlyTransaction();
    return transaction.GetColumn(column).TryGet(in key, out value);
  }

  /// <summary>Checks the key in a new database snapshot.</summary>
  public static bool ContainsKey<TKey, TValue>(
      this IDistributedColumn<TKey, TValue> column, in TKey key)
  {
    using var transaction = column.Database.BeginReadOnlyTransaction();
    return transaction.GetColumn(column).ContainsKey(in key);
  }

  /// <summary>Writes the key in its own transaction, retrying on conflicts.</summary>
  public static void Upsert<TKey, TValue>(
      this IDistributedColumn<TKey, TValue> column, in TKey key, in TValue value)
  {
    var k = key;
    var v = value;
    column.Database.Execute(transaction => transaction.GetColumn(column).Upsert(k, v),
        TransactionIsolationLevel.Snapshot);
  }

  /// <summary>Deletes the key in its own transaction; returns whether it existed.</summary>
  public static bool TryDelete<TKey, TValue>(
      this IDistributedColumn<TKey, TValue> column, in TKey key)
  {
    var k = key;
    return column.Database.Execute(transaction =>
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
      this IDistributedColumn<TKey, TValue> column,
      in TKey key,
      in TValue valueToAdd,
      Func<TValue, TValue> update)
  {
    ArgumentNullException.ThrowIfNull(update);
    var k = key;
    var added = valueToAdd;
    return column.Database.Execute(transaction =>
    {
      var view = transaction.GetColumn(column);
      var value = view.TryGet(k, out var current) ? update(current) : added;
      view.Upsert(k, value);
      return value;
    }, TransactionIsolationLevel.Snapshot);
  }
}
