namespace ZoneTree.Transactional;

/// <summary>
/// Configures a transaction iterator.
/// </summary>
public sealed class TransactionIteratorOptions
{
  /// <summary>
  /// If true, disk segment reads performed by the iterator contribute to the block cache, so later reads of the same
  /// blocks are served from memory. A maintainer removes the blocks that stay inactive. True by default, as it is for
  /// iterators created without options and for ranges. Set it to false for a one-pass scan over much of the data,
  /// such as an export or a full count, so that the scan does not fill the cache.
  /// </summary>
  public bool ContributeToTheBlockCache { get; set; } = true;

  /// <summary>
  /// The number of sequential disk-segment records the iterator may prefetch
  /// at once. Values less than two disable prefetching.
  /// </summary>
  public int DiskSegmentPrefetchSize { get; set; }
}
