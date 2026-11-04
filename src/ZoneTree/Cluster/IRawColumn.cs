namespace ZoneTree.Cluster;

/// <summary>A non-transactional column with direct reads and writes.</summary>
/// <remarks>Writes are independent of cluster transactions. The cluster owns the column's lifetime.</remarks>
public interface IRawColumn<TKey, TValue>
{
  string Name { get; }
  IZoneTreeCluster Cluster { get; }
  IZoneTreeMaintenance<TKey, TValue> Maintenance { get; }
  IMaintainer CreateMaintainer();

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
  IZoneTreeIterator<TKey, TValue> CreateIterator(IteratorOptions options = null);
  IZoneTreeIterator<TKey, TValue> CreateReverseIterator(IteratorOptions options = null);
  IAsyncIterator<TKey, TValue> CreateAsyncIterator(IteratorOptions options = null);
  IAsyncIterator<TKey, TValue> CreateAsyncReverseIterator(IteratorOptions options = null);

  /// <summary>Makes writes completed before this call durable across power loss or an OS crash.</summary>
  Task SyncWALAsync(CancellationToken cancellationToken = default);
}
