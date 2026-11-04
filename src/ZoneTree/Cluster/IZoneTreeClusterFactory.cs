using ZoneTree.Backup;

namespace ZoneTree.Cluster;

/// <summary>Creates, opens and restores in-process distributed databases.</summary>
public interface IZoneTreeClusterFactory
{
  /// <summary>Creates a cluster; fails if the cluster already exists.</summary>
  IZoneTreeCluster CreateInProcess(ZoneTreeClusterOptions options);

  /// <summary>Opens an existing cluster; fails if it does not exist.</summary>
  IZoneTreeCluster OpenInProcess(ZoneTreeClusterOptions options);

  /// <summary>
  /// Creates a new cluster or opens an existing one with the exact declared schema.
  /// </summary>
  IZoneTreeCluster OpenOrCreateInProcess(ZoneTreeClusterOptions options);

  /// <summary>
  /// Restores and opens a cluster from local backup. Null selects the latest generation.
  /// </summary>
  ValueTask<IZoneTreeCluster> RestoreInProcessAsync(
      ZoneTreeClusterOptions options,
      LocalLiveBackupOptions backup,
      long? generationId = null,
      CancellationToken cancellationToken = default);

  /// <summary>Restores a complete cluster generation from custom object storage.</summary>
  /// <inheritdoc cref="RestoreInProcessAsync(ZoneTreeClusterOptions, LocalLiveBackupOptions, long?, CancellationToken)"/>
  ValueTask<IZoneTreeCluster> RestoreInProcessAsync(
      ZoneTreeClusterOptions options,
      IBackupObjectStorage storage,
      ObjectStorageLiveBackupOptions backupOptions = null,
      long? generationId = null,
      CancellationToken cancellationToken = default);
}
