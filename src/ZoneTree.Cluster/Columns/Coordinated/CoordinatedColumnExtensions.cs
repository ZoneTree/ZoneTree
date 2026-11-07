using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>
/// Single-column convenience operations, each using its own domain transaction.
/// Use explicit domain transactions to batch work or change several columns atomically.
/// </summary>
public static class CoordinatedColumnExtensions
{
  // Each convenience method reads and writes one key, and writing the key detects the
  // conflicts on it, so snapshot isolation is enough.
  static readonly ClusterTransactionOptions SnapshotWrite = new() { IsolationLevel = TransactionIsolationLevel.Snapshot };

  /// <summary>Reads the key in a new domain snapshot.</summary>
  public static bool TryGet<TKey, TValue>(
      this ICoordinatedColumn<TKey, TValue> column, in TKey key, out TValue value)
  {
    using var transaction = column.Domain.BeginReadOnlyTransaction();
    return transaction.GetColumn(column).TryGet(in key, out value);
  }

  /// <summary>Checks the key in a new domain snapshot.</summary>
  public static bool ContainsKey<TKey, TValue>(
      this ICoordinatedColumn<TKey, TValue> column, in TKey key)
  {
    using var transaction = column.Domain.BeginReadOnlyTransaction();
    return transaction.GetColumn(column).ContainsKey(in key);
  }

  /// <summary>Writes the key in its own transaction, retrying on conflicts.</summary>
  public static void Upsert<TKey, TValue>(
      this ICoordinatedColumn<TKey, TValue> column, in TKey key, in TValue value)
  {
    var k = key;
    var v = value;
    column.Domain.Execute(transaction => transaction.GetColumn(column).Upsert(k, v),
        SnapshotWrite);
  }

  /// <summary>Deletes the key in its own transaction; returns whether it existed.</summary>
  public static bool TryDelete<TKey, TValue>(
      this ICoordinatedColumn<TKey, TValue> column, in TKey key)
  {
    var k = key;
    return column.Domain.Execute(transaction =>
    {
      var view = transaction.GetColumn(column);
      if (!view.ContainsKey(k))
        return false;
      view.Delete(k);
      return true;
    }, SnapshotWrite);
  }

  /// <summary>
  /// Adds or updates the key in its own transaction. Conflicts rerun the update
  /// delegate, which must not have external side effects. Returns the written value.
  /// </summary>
  public static TValue AddOrUpdate<TKey, TValue>(
      this ICoordinatedColumn<TKey, TValue> column,
      in TKey key,
      in TValue valueToAdd,
      Func<TValue, TValue> update)
  {
    ArgumentNullException.ThrowIfNull(update);
    var k = key;
    var added = valueToAdd;
    return column.Domain.Execute(transaction =>
    {
      var view = transaction.GetColumn(column);
      var value = view.TryGet(k, out var current) ? update(current) : added;
      view.Upsert(k, value);
      return value;
    }, SnapshotWrite);
  }

  /// <summary>Asynchronously reads the key in a new domain snapshot.</summary>
  public static async Task<ReadResult<TValue>> TryGetAsync<TKey, TValue>(
      this ICoordinatedColumn<TKey, TValue> column,
      TKey key,
      CancellationToken cancellationToken = default)
  {
    await using var transaction = await column.Domain.BeginReadOnlyTransactionAsync(
        cancellationToken).ConfigureAwait(false);
    return await transaction.GetColumn(column).TryGetAsync(key, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Asynchronously checks the key in a new domain snapshot.</summary>
  public static async Task<bool> ContainsKeyAsync<TKey, TValue>(
      this ICoordinatedColumn<TKey, TValue> column,
      TKey key,
      CancellationToken cancellationToken = default)
  {
    await using var transaction = await column.Domain.BeginReadOnlyTransactionAsync(
        cancellationToken).ConfigureAwait(false);
    return await transaction.GetColumn(column).ContainsKeyAsync(key, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Asynchronously writes and commits the key for process-crash durability,
  /// retrying only on conflicts. Use a cluster transaction for power-loss durability.
  /// </summary>
  public static Task UpsertAsync<TKey, TValue>(
      this ICoordinatedColumn<TKey, TValue> column,
      TKey key,
      TValue value,
      CancellationToken cancellationToken = default)
      => column.Domain.ExecuteAsync((transaction, token) =>
          transaction.GetColumn(column).UpsertAsync(key, value, token),
          SnapshotWrite, cancellationToken: cancellationToken);

  /// <summary>Asynchronously deletes and commits the key; returns whether it existed.</summary>
  public static Task<bool> TryDeleteAsync<TKey, TValue>(
      this ICoordinatedColumn<TKey, TValue> column,
      TKey key,
      CancellationToken cancellationToken = default)
      => column.Domain.ExecuteAsync(async (transaction, token) =>
      {
        var view = transaction.GetColumn(column);
        if (!await view.ContainsKeyAsync(key, token).ConfigureAwait(false))
          return false;
        await view.DeleteAsync(key, token).ConfigureAwait(false);
        return true;
      }, SnapshotWrite, cancellationToken: cancellationToken);

  /// <summary>
  /// Asynchronously adds or updates and commits the key. Conflicts rerun the synchronous
  /// update delegate, which must have no external side effects. Returns the written value.
  /// </summary>
  public static Task<TValue> AddOrUpdateAsync<TKey, TValue>(
      this ICoordinatedColumn<TKey, TValue> column,
      TKey key,
      TValue valueToAdd,
      Func<TValue, TValue> update,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(update);
    return column.Domain.ExecuteAsync(async (transaction, token) =>
    {
      var view = transaction.GetColumn(column);
      var current = await view.TryGetAsync(key, token).ConfigureAwait(false);
      var value = current.Found ? update(current.Value) : valueToAdd;
      await view.UpsertAsync(key, value, token).ConfigureAwait(false);
      return value;
    }, SnapshotWrite, cancellationToken: cancellationToken);
  }
}
