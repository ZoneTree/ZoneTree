using ZoneTree.Backup;

namespace ZoneTree.Transactional.Distributed;

/// <summary>
/// Scheduling and transfer settings for consistent database-wide live backups.
/// Each generation always includes in-memory records and transaction recovery state.
/// Local and object-storage retention are configured by their existing storage options.
/// </summary>
public sealed class DistributedLiveBackupOptions
{
  /// <summary>Queues a database generation after a column or coordinator merge.</summary>
  public bool BackupAfterMerge { get; init; } = true;

  /// <summary>The optional UTC schedule for complete database generations.</summary>
  public LiveBackupSchedule Schedule { get; init; } = LiveBackupSchedule.None;

  /// <summary>Compression of backup-created in-memory record batches.</summary>
  public LiveBackupCompressionOptions RecordBatchCompression { get; init; } = new();

  /// <summary>The database-wide limit on concurrent backup file transfers.</summary>
  public int MaxConcurrentFileTransfers { get; init; } = 8;
}
