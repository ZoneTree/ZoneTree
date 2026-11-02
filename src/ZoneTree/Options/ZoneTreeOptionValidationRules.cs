namespace ZoneTree.Options;

internal static class ZoneTreeOptionValidationRules
{
  public static readonly Rule MutableSegmentMaxItemCount =
      Rule.Min(1000);

  public static readonly Rule MutableSegmentBloomFilterBitsPerItem =
      Rule.Min(0).Max(64);

  public static readonly Rule DiskSegmentMaxItemCount =
      Rule.Min(10000);

  public static readonly Rule BTreeNodeSize =
      Rule.Min(16);

  public static readonly Rule BTreeLeafSize =
      Rule.Min(16);

  public static readonly Rule WriteAheadLogCompressionBlockSize =
      Rule.MinKB(1).MaxMB(16);

  public static readonly Rule DiskSegmentCompressionBlockSize =
      Rule.MinMB(1).MaxMB(64);

  public static readonly Rule WriteAheadLogEmptyQueuePollInterval =
      Rule.MinSeconds(0);

  public static readonly Rule WriteAheadLogTailWriterJobInterval =
      Rule.MinSeconds(0);

  public static readonly Rule DiskSegmentMinimumRecordCount =
      Rule.Min(1000);

  public static readonly Rule DiskSegmentMaximumRecordCount =
      Rule.Min(2000);

  public static readonly Rule DiskSegmentKeyCacheSize =
      Rule.Min(0);

  public static readonly Rule DiskSegmentValueCacheSize =
      Rule.Min(0);

  public static readonly Rule DiskSegmentMaterializedEntryCacheSize =
      Rule.Min(0);

  public static readonly Rule DiskSegmentSearchHintPrefetchSize =
      Rule.Min(0);

  public static readonly Rule DiskSegmentKeyCacheRecordLifeTimeInMillisecond =
      Rule.MinSeconds(0);

  public static readonly Rule DiskSegmentValueCacheRecordLifeTimeInMillisecond =
      Rule.MinSeconds(0);

  public static readonly Rule DiskSegmentDefaultSparseArrayStepSize =
      Rule.Min(0);
}
