namespace ZoneTree.Transactional;

/// <summary>
/// Configures a transaction iterator.
/// </summary>
public sealed class TransactionIteratorOptions
{
  /// <summary>
  /// If true, disk segment reads performed by the iterator contribute to the
  /// block cache.
  /// </summary>
  public bool ContributeToTheBlockCache { get; set; }

  /// <summary>
  /// The number of sequential disk-segment records the iterator may prefetch
  /// at once. Values less than two disable prefetching.
  /// </summary>
  public int DiskSegmentPrefetchSize { get; set; }
}
