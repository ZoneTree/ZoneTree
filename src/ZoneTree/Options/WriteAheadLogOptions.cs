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
  /// based on this setting. Default value is AsyncCompressed.
  /// </summary>
  public WriteAheadLogMode WriteAheadLogMode { get; set; }
      = WriteAheadLogDefaultValues.WriteAheadLogMode;

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
  /// The compression method for the WALs with compression enabled.
  /// Default is Zstd.
  /// </summary>
  public CompressionMethod CompressionMethod { get; set; } = WriteAheadLogDefaultValues.CompressionMethod;

  /// <summary>
  /// The compression level of the selected compression method.
  /// Default is <see cref="CompressionLevels.Zstd0"/>.
  /// </summary>
  public int CompressionLevel { get; set; } = WriteAheadLogDefaultValues.CompressionLevel;

  /// <summary>
  /// Options for sync-compressed mode.
  /// </summary>
  public SyncCompressedModeOptions SyncCompressedModeOptions { get; set; } = new();

  /// <summary>
  /// Options for async-compressed mode.
  /// </summary>
  public AsyncCompressedModeOptions AsyncCompressedModeOptions { get; set; } = new();

  /// <summary>
  /// Incremental backup is a WAL feature which moves
  /// all WAL data to another incremental log file when it is compacted.
  /// It is required to compact WAL in memory without data loss in
  /// persistent device. Enabling backup makes WAL compaction slower.
  /// Default value is false.
  /// </summary>
  public bool EnableIncrementalBackup { get; set; }
}
