using ZoneTree.Backup;

namespace ZoneTree.Cluster;

/// <summary>Creates, opens and restores clusters hosted in this process, and connects to remote clusters.</summary>
public interface IZoneTreeClusterFactory
{
  /// <summary>Creates a cluster; fails if the cluster already exists.</summary>
  IZoneTreeClusterHost CreateInProcess(ZoneTreeClusterOptions options);

  /// <summary>Opens an existing cluster; fails if it does not exist.</summary>
  IZoneTreeClusterHost OpenInProcess(ZoneTreeClusterOptions options);

  /// <summary>
  /// Creates a new cluster or opens an existing one with the exact declared schema.
  /// </summary>
  IZoneTreeClusterHost OpenOrCreateInProcess(ZoneTreeClusterOptions options);

  /// <summary>
  /// Restores and opens a cluster from local backup. Null selects the latest generation.
  /// </summary>
  ValueTask<IZoneTreeClusterHost> RestoreInProcessAsync(
      ZoneTreeClusterOptions options,
      LocalLiveBackupOptions backup,
      long? generationId = null,
      CancellationToken cancellationToken = default);

  /// <summary>Restores a complete cluster generation from custom object storage.</summary>
  /// <inheritdoc cref="RestoreInProcessAsync(ZoneTreeClusterOptions, LocalLiveBackupOptions, long?, CancellationToken)"/>
  ValueTask<IZoneTreeClusterHost> RestoreInProcessAsync(
      ZoneTreeClusterOptions options,
      IBackupObjectStorage storage,
      ObjectStorageLiveBackupOptions backupOptions = null,
      long? generationId = null,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Connects to a cluster hosted by other processes or machines. The client has the
  /// same API as a cluster in this process; storage administration stays with the hosts.
  /// </summary>
  ValueTask<IZoneTreeCluster> ConnectAsync(
      ZoneTreeClusterConnectionOptions options,
      CancellationToken cancellationToken = default);
}
