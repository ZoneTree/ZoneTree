namespace ZoneTree.Options;

public static class WriteAheadLogDefaultValues
{
  public static readonly WriteAheadLogMode WriteAheadLogMode = WriteAheadLogMode.AsyncPrivate;

  public static readonly int CompressionBlockSize = 1024 * 32 * 8;

  public static readonly CompressionMethod CompressionMethod = CompressionMethod.Zstd;

  public static readonly int CompressionLevel = CompressionLevels.Zstd1;

  public static readonly bool PrivateModeEnableTailWriterJob = true;

  public static readonly int PrivateModeTailWriterJobInterval = 500;

  public static readonly int AsyncPrivateModeEmptyQueuePollInterval = 100;
}
