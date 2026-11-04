using ZoneTree.Backup;

namespace ZoneTree.Transactional.Distributed;

/// <summary>
/// Scheduling and transfer settings for consistent database-wide live backups.
/// </summary>
public sealed class DistributedLiveBackupOptions
{
  /// <summary>Whether to create a backup generation after a merge.</summary>
  public bool BackupAfterMerge { get; init; } = true;

  /// <summary>The optional UTC schedule for complete database generations.</summary>
  public LiveBackupSchedule Schedule { get; init; } = LiveBackupSchedule.None;

  /// <summary>Compression of backup-created in-memory record batches.</summary>
  public LiveBackupCompressionOptions RecordBatchCompression { get; init; } = new();

  /// <summary>The database-wide limit on concurrent backup file transfers.</summary>
  public int MaxConcurrentFileTransfers { get; init; } = 8;
}
