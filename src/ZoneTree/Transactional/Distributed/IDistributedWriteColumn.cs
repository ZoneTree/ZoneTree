namespace ZoneTree.Transactional.Distributed;

/// <summary>Reads and writes one typed column inside a database transaction.</summary>
public interface IDistributedWriteColumn<TKey, TValue> : IDistributedReadColumn<TKey, TValue>
{
  /// <summary>
  /// Reads the key and protects it against concurrent changes, including when absent.
  /// </summary>
  bool TryGetForUpdate(in TKey key, out TValue value);

  /// <summary>Asynchronously reads and protects the key against concurrent changes.</summary>
  ValueTask<DistributedReadResult<TValue>> TryGetForUpdateAsync(
      TKey key, CancellationToken cancellationToken = default);

  /// <summary>
  /// Inserts or updates the key within the transaction.
  /// </summary>
  void Upsert(in TKey key, in TValue value);

  /// <summary>
  /// Asynchronously inserts or updates the key within the transaction.
  /// </summary>
  ValueTask UpsertAsync(TKey key, TValue value, CancellationToken cancellationToken = default);

  /// <summary>Deletes the key within the transaction.</summary>
  void Delete(in TKey key);

  /// <summary>Asynchronously deletes the key within the transaction.</summary>
  ValueTask DeleteAsync(TKey key, CancellationToken cancellationToken = default);
}
