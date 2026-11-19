namespace ZoneTree.Options;

public static class DiskSegmentDefaultValues
{
  public static readonly DiskSegmentMode Mode = DiskSegmentMode.MultiPartDiskSegment;

  public static readonly int CompressionBlockSize = 4 * 1024 * 1024;

  public static readonly CompressionMethod CompressionMethod = CompressionMethod.Zstd;

  public static readonly int CompressionLevel = CompressionLevels.Zstd1;

  public static readonly int MaximumRecordCount = 3_000_000;

  public static readonly int MinimumRecordCount = 1_500_000;

  public static readonly int KeyCacheSize = 1024;

  public static readonly int ValueCacheSize = 1024;

  public static readonly int MaterializedEntryCacheSize = 65536;

  public static readonly int SearchHintPrefetchSize = 16;

  public static readonly TimeSpan KeyCacheRecordLifetime = TimeSpan.FromSeconds(10);

  public static readonly TimeSpan ValueCacheRecordLifetime = TimeSpan.FromSeconds(10);

  public static readonly int DefaultSparseArrayStepSize = 1024;
}
