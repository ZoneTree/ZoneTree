namespace ZoneTree.Transactional.Distributed;

/// <summary>A typed column view borrowing its database transaction's snapshot.</summary>
/// <remarks>
/// This view has no separate lifetime or commit. Reads and iterator semantics match
/// IReadTransaction, including own-write visibility and serializable range tracking.
/// Data access is rejected after its transaction ends.
/// </remarks>
public interface IDistributedReadColumn<TKey, TValue>
{
  /// <summary>Checks whether the key exists in the transaction's snapshot.</summary>
  bool ContainsKey(in TKey key);

  /// <summary>Reads the snapshot plus this transaction's own writes to this column.</summary>
  bool TryGet(in TKey key, out TValue value);

  /// <summary>
  /// Lazily reads an inclusive, comparer-ordered range. A higher lower bound is empty.
  /// Enumerate before the transaction ends. It sees own writes made before enumeration
  /// starts. Serializable transactions track the consumed range, or the entire range
  /// when exhausted, including gaps and missing keys, exactly as single-tree ranges do.
  /// </summary>
  IEnumerable<KeyValuePair<TKey, TValue>> Range(
      in TKey from, in TKey to, bool reverse = false);

  /// <summary>
  /// Creates an ascending iterator, disposed by its caller or when the transaction
  /// ends. Each seek, or the first Next, captures own writes made before that position.
  /// Serializable transactions track the consumed key ranges in this column.
  /// </summary>
  ITransactionIterator<TKey, TValue> CreateIterator(TransactionIteratorOptions options = null);

  /// <summary>Creates a descending iterator with the same lifetime and tracking rules.</summary>
  ITransactionIterator<TKey, TValue> CreateReverseIterator(TransactionIteratorOptions options = null);
}
