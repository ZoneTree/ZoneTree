namespace ZoneTree.Transactional;

/// <summary>
/// Configures a transaction iterator.
/// </summary>
public sealed class TransactionIteratorOptions
{
  /// <summary>
  /// If true, disk segment reads performed by the iterator contribute to the
  /// block cache. A maintainer removes the blocks that stay inactive.
  /// Default is true, as it is for iterators created without options and for ranges.
  /// </summary>
  public bool ContributeToTheBlockCache { get; set; } = true;

  /// <summary>
  /// The number of sequential disk-segment records the iterator may prefetch
  /// at once. Values less than two disable prefetching.
  /// </summary>
  public int DiskSegmentPrefetchSize { get; set; }
}
