namespace ZoneTree.Transactional.Distributed;

/// <summary>Reads and writes one typed column inside a database transaction.</summary>
public interface IDistributedWriteColumn<TKey, TValue> : IDistributedReadColumn<TKey, TValue>
{
  /// <summary>
  /// Reads and protects the key, including its absence, like a write at either
  /// isolation level. A writing transaction fails validation if another transaction
  /// committed or prepared a write to this column/key after its snapshot. Protection
  /// still applies when this transaction's writes are only in other columns.
  /// </summary>
  bool TryGetForUpdate(in TKey key, out TValue value);

  /// <summary>
  /// Writes a version in this column. A detected conflict sets the parent transaction's
  /// HasConflict flag and suppresses further writes across all its columns.
  /// </summary>
  void Upsert(in TKey key, in TValue value);

  /// <summary>Deletes the key with the same conflict rules as Upsert.</summary>
  void Delete(in TKey key);
}
