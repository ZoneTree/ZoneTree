namespace ZoneTree.Transactional;

/// <summary>
/// A transaction that reads a consistent snapshot of a transactional ZoneTree.
/// </summary>
/// <remarks>
/// It reads the snapshot taken when it began. It is not thread-safe.
/// Dispose it when its work is done: merges keep the versions an open transaction can read.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IReadTransaction<TKey, TValue> : IDisposable
{
  /// <summary>
  /// The timestamp of the snapshot the transaction reads:
  /// the commit timestamp of the latest commit it sees.
  /// </summary>
  long SnapshotTimestamp { get; }

  /// <summary>
  /// Checks if the key exists in the snapshot of the transaction.
  /// </summary>
  bool ContainsKey(in TKey key);

  /// <summary>
  /// Gets the value of the key in the snapshot of the transaction.
  /// </summary>
  bool TryGet(in TKey key, out TValue value);

  /// <summary>
  /// Reads the records whose keys are between two keys, inclusive.
  /// </summary>
  /// <remarks>
  /// The records are read lazily while the result is enumerated, which must happen
  /// before the transaction ends. The enumeration sees the transaction's writes made
  /// before it started. In a serializable transaction the range joins the read set:
  /// up to the last key read when the enumeration stops early, the whole range otherwise.
  /// </remarks>
  /// <param name="from">The lowest key.</param>
  /// <param name="to">The highest key. A range with a higher lowest key is empty.</param>
  /// <param name="reverse">True to read in descending key order.</param>
  /// <returns>The records in key order.</returns>
  IEnumerable<KeyValuePair<TKey, TValue>> Range(in TKey from, in TKey to, bool reverse = false);

  /// <summary>
  /// Creates an iterator over the snapshot of the transaction in ascending key order.
  /// In a serializable transaction, the iterated ranges join the read set.
  /// </summary>
  /// <remarks>
  /// The iterator sees the transaction's writes made before each Seek, SeekFirst or
  /// first Next call. It must be disposed, and it cannot be used after the transaction ends.
  /// </remarks>
  ITransactionIterator<TKey, TValue> CreateIterator(TransactionIteratorOptions options = null);

  /// <summary>
  /// Creates an iterator over the snapshot of the transaction in descending key order.
  /// </summary>
  /// <inheritdoc cref="CreateIterator"/>
  ITransactionIterator<TKey, TValue> CreateReverseIterator(TransactionIteratorOptions options = null);
}
