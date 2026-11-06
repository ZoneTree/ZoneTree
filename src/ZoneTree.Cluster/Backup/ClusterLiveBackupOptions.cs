using ZoneTree.Backup;

namespace ZoneTree.Cluster;

/// <summary>
/// Scheduling and transfer settings for consistent cluster-wide live backups.
/// </summary>
public sealed class ClusterLiveBackupOptions
{
  /// <summary>Whether to create a backup generation after a merge.</summary>
  public bool BackupAfterMerge { get; init; } = true;

  /// <summary>The optional UTC schedule for complete cluster generations.</summary>
  public LiveBackupSchedule Schedule { get; init; } = LiveBackupSchedule.None;

  /// <summary>Compression of backup-created in-memory record batches.</summary>
  public LiveBackupCompressionOptions RecordBatchCompression { get; init; } = new();

  /// <summary>The cluster-wide limit on concurrent backup file transfers.</summary>
  public int MaxConcurrentFileTransfers { get; init; } = 8;
}
