using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>A non-transactional column with direct reads and writes.</summary>
/// <remarks>
/// Writes are independent of cluster transactions. The cluster owns the column's lifetime.
/// In a remote cluster the synchronous methods block the calling thread for a network round trip.
/// </remarks>
public interface IRawColumn<TKey, TValue>
{
  string Name { get; }
  IZoneTreeCluster Cluster { get; }
  bool ContainsKey(in TKey key);
  ValueTask<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default);
  bool TryGet(in TKey key, out TValue value);
  ValueTask<ReadResult<TValue>> TryGetAsync(TKey key, CancellationToken cancellationToken = default);
  void Upsert(in TKey key, in TValue value);
  ValueTask UpsertAsync(TKey key, TValue value, CancellationToken cancellationToken = default);
  void Delete(in TKey key);
  ValueTask DeleteAsync(TKey key, CancellationToken cancellationToken = default);

  /// <summary>Reads an inclusive, comparer-ordered range.</summary>
  IEnumerable<KeyValuePair<TKey, TValue>> Range(in TKey from, in TKey to, bool reverse = false);
  IAsyncEnumerable<KeyValuePair<TKey, TValue>> RangeAsync(
      TKey from, TKey to, bool reverse = false, CancellationToken cancellationToken = default);
  ITransactionIterator<TKey, TValue> CreateIterator(TransactionIteratorOptions options = null);
  ITransactionIterator<TKey, TValue> CreateReverseIterator(TransactionIteratorOptions options = null);
  IAsyncTransactionIterator<TKey, TValue> CreateAsyncIterator(TransactionIteratorOptions options = null);
  IAsyncTransactionIterator<TKey, TValue> CreateAsyncReverseIterator(TransactionIteratorOptions options = null);

  /// <summary>Makes writes completed before this call durable across power loss or an OS crash.</summary>
  Task SyncWALAsync(CancellationToken cancellationToken = default);
}
