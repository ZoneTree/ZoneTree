using ZoneTree.Options;

namespace ZoneTree.Backup;

/// <summary>
/// Options for ZoneTree live backup.
/// </summary>
public sealed class LiveBackupOptions
{
  /// <summary>
  /// Backup store implementation.
  /// </summary>
  public ILiveBackupStore Store { get; set; }

  /// <summary>
  /// If true, successful normal merge operations queue a complete backup generation.
  /// </summary>
  public bool BackupAfterMerge { get; set; } = true;

  /// <summary>
  /// Optional UTC schedule for automatic complete backup generations.
  /// </summary>
  public LiveBackupSchedule Schedule { get; set; } = LiveBackupSchedule.None;

  /// <summary>
  /// If true, the current mutable and read-only in-memory records are streamed
  /// through a ZoneTree in-memory iterator.
  /// Transactional backups require this to be true to preserve transaction outcomes.
  /// </summary>
  public bool IncludeInMemoryRecords { get; set; } = true;

  /// <summary>
  /// Controls how in-memory records are collected for each backup generation.
  /// Transactional backups always capture a frozen snapshot, regardless of this setting.
  /// </summary>
  public LiveBackupInMemoryMode InMemoryMode { get; set; }
      = LiveBackupInMemoryMode.Live;

  /// <summary>
  /// Compression options for backup-created in-memory record batch files.
  /// Physical disk segment files are copied as-is.
  /// </summary>
  public LiveBackupCompressionOptions RecordBatchCompression { get; set; } = new();

  /// <summary>
  /// Maximum number of file transfers that may run concurrently during live backup.
  /// </summary>
  public int MaxConcurrentFileTransfers { get; set; } = 8;

  public void Normalize()
  {
    Schedule ??= LiveBackupSchedule.None;
    RecordBatchCompression ??= new LiveBackupCompressionOptions();
    RecordBatchCompression.Normalize();
    if (MaxConcurrentFileTransfers <= 0)
      MaxConcurrentFileTransfers = 8;
  }
}

public enum LiveBackupInMemoryMode
{
  /// <summary>
  /// Iterates mutable and read-only in-memory segments without moving the
  /// mutable segment.
  /// </summary>
  Live,

  /// <summary>
  /// Moves the mutable segment forward and iterates read-only in-memory
  /// segments only.
  /// </summary>
  Snapshot
}

public sealed class LiveBackupCompressionOptions
{
  public const CompressionMethod DefaultMethod = CompressionMethod.Zstd;

  public const int DefaultLevel = CompressionLevels.Zstd0;

  public const int DefaultBlockSize = 1024 * 1024;

  /// <summary>
  /// Compression method used for in-memory record batch files.
  /// </summary>
  public CompressionMethod Method { get; set; } = DefaultMethod;

  /// <summary>
  /// Compression level used for in-memory record batch files.
  /// </summary>
  public int Level { get; set; } = DefaultLevel;

  /// <summary>
  /// Uncompressed block size for record batch compression.
  /// </summary>
  public int BlockSize { get; set; } = DefaultBlockSize;

  public void Normalize()
  {
    if (BlockSize <= 0)
      BlockSize = DefaultBlockSize;

    if (!CompressionLevels.IsValid(Method, Level))
    {
      Method = DefaultMethod;
      Level = DefaultLevel;
    }
  }
}
