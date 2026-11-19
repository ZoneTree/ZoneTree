using ZoneTree.WAL;

namespace ZoneTree.Options;

/// <summary>
/// Write Ahead Log Options. The options will be used
/// for creation of new Write Ahead Logs.
/// Existing WALs will be created with their existing options.
/// </summary>
public sealed class WriteAheadLogOptions
{
  /// <summary>
  /// The default write ahead log mode. New WALs will be created
  /// based on this setting. Default value is AsyncPrivate.
  /// </summary>
  public WriteAheadLogMode Mode { get; set; }
      = WriteAheadLogDefaultValues.Mode;

  /// <summary>
  /// The custom options are reserved to be used
  /// by custom write ahead log providers.
  /// </summary>
  public object CustomOptions { get; set; }

  /// <summary>
  /// WAL compression block size, in bytes. New WALs are created based on
  /// this setting. Larger blocks may improve compression ratio, but they also
  /// increase the memory needed to buffer, compress, and decompress WAL blocks.
  /// Default value is 256 KB.
  /// </summary>
  public int CompressionBlockSize { get; set; }
      = WriteAheadLogDefaultValues.CompressionBlockSize;

  /// <summary>
  /// The compression method of the WAL blocks.
  /// Default is Zstd.
  /// </summary>
  public CompressionMethod CompressionMethod { get; set; } = WriteAheadLogDefaultValues.CompressionMethod;

  /// <summary>
  /// The compression level of the selected compression method.
  /// Default is <see cref="CompressionLevels.Zstd1"/>.
  /// </summary>
  public int CompressionLevel { get; set; } = WriteAheadLogDefaultValues.CompressionLevel;

  /// <summary>
  /// In the Private and Shared modes, if enabled, a background job syncs the
  /// dirty WAL to the disk periodically. This limits what a power loss can lose.
  /// Process crashes do not depend on it.
  /// Default value is true.
  /// </summary>
  public bool EnableSyncJob { get; set; } = WriteAheadLogDefaultValues.EnableSyncJob;

  /// <summary>
  /// The delay before the next run of the sync job.
  /// Default value is 500 ms.
  /// </summary>
  public TimeSpan SyncJobInterval { get; set; } = WriteAheadLogDefaultValues.SyncJobInterval;

  /// <summary>
  /// In the AsyncPrivate and AsyncShared modes, the delay before the background
  /// writer polls its queue again when the queue is empty.
  /// Default value is 100 ms.
  /// </summary>
  public TimeSpan EmptyQueuePollInterval { get; set; } = WriteAheadLogDefaultValues.EmptyQueuePollInterval;

  /// <summary>
  /// Options for the shared journal modes. Compression settings above also
  /// apply. Caller-side and queued writes use Shared and AsyncShared respectively.
  /// </summary>
  public SharedModeOptions SharedModeOptions { get; set; } = new();

  /// <summary>
  /// Returns a copy of the options. The copy shares <see cref="CustomOptions"/>.
  /// </summary>
  public WriteAheadLogOptions Clone()
  {
    var clone = (WriteAheadLogOptions)MemberwiseClone();
    clone.SharedModeOptions = SharedModeOptions?.Clone();
    return clone;
  }
}
