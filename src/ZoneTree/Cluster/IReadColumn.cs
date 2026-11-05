using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>A typed column view borrowing its cluster transaction's snapshot.</summary>
/// <remarks>
/// Reads use the parent transaction's snapshot and include its own writes.
/// Use the synchronous methods of each view sequentially and only while its transaction
/// is active; its asynchronous methods run in call order. In a remote cluster the
/// synchronous methods block the calling thread for a network round trip.
/// </remarks>
public interface IReadColumn<TKey, TValue>
{
  /// <summary>Checks whether the key exists in the transaction's snapshot.</summary>
  bool ContainsKey(in TKey key);

  /// <summary>Asynchronously checks whether the key exists.</summary>
  ValueTask<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default);

  /// <summary>Reads the snapshot plus this transaction's own writes to this column.</summary>
  bool TryGet(in TKey key, out TValue value);

  /// <summary>Asynchronously reads the same snapshot and own writes; Found distinguishes a missing key.</summary>
  ValueTask<ReadResult<TValue>> TryGetAsync(
      TKey key, CancellationToken cancellationToken = default);

  /// <summary>
  /// Reads an inclusive, comparer-ordered range. Enumerate before the transaction ends.
  /// </summary>
  IEnumerable<KeyValuePair<TKey, TValue>> Range(
      in TKey from, in TKey to, bool reverse = false);

  /// <summary>
  /// Asynchronously reads an inclusive, comparer-ordered range.
  /// </summary>
  IAsyncEnumerable<KeyValuePair<TKey, TValue>> RangeAsync(
      TKey from, TKey to, bool reverse = false, CancellationToken cancellationToken = default);

  /// <summary>
  /// Creates an iterator in ascending key order.
  /// </summary>
  ITransactionIterator<TKey, TValue> CreateIterator(TransactionIteratorOptions options = null);

  /// <summary>
  /// Creates an asynchronous iterator in ascending key order.
  /// </summary>
  IAsyncTransactionIterator<TKey, TValue> CreateAsyncIterator(
      TransactionIteratorOptions options = null);

  /// <summary>Creates an iterator in descending key order.</summary>
  ITransactionIterator<TKey, TValue> CreateReverseIterator(TransactionIteratorOptions options = null);

  /// <summary>Creates an asynchronous iterator in descending key order.</summary>
  IAsyncTransactionIterator<TKey, TValue> CreateAsyncReverseIterator(
      TransactionIteratorOptions options = null);
}
