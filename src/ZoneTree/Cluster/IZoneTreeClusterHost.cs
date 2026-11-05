using ZoneTree.Backup;
using ZoneTree.Logger;

namespace ZoneTree.Cluster;

/// <summary>
/// The owner of a cluster's storage in this process: a client of the cluster, plus
/// storage maintenance, administration and backups.
/// </summary>
/// <remarks>
/// Disposing the host closes its storage, and disposing or dropping it rolls back the
/// active transactions.
/// </remarks>
public interface IZoneTreeClusterHost : IZoneTreeCluster
{
  /// <summary>
  /// The background WAL sync interval. Zero disables automatic syncing.
  /// </summary>
  TimeSpan AutoSyncInterval { get; set; }

  /// <summary>
  /// Prevents new writing transactions and raw column writes when set.
  /// </summary>
  bool IsReadOnly { get; set; }

  /// <summary>The cluster logger.</summary>
  ILogger Logger { get; }

  /// <summary>Cluster-wide maintenance and diagnostics.</summary>
  IZoneTreeClusterMaintenance Maintenance { get; }

  /// <summary>
  /// Creates a maintainer for every column of the cluster.
  /// </summary>
  IMaintainer CreateMaintainer();

  /// <summary>Creates a maintainer for one column of any kind.</summary>
  /// <exception cref="KeyNotFoundException">The column does not exist.</exception>
  IMaintainer CreateColumnMaintainer(string name);

  /// <summary>Gets the storage maintenance of a coordinated column.</summary>
  /// <exception cref="KeyNotFoundException">The column does not exist.</exception>
  /// <exception cref="InvalidOperationException">The column has a different kind or types.</exception>
  ITransactionalZoneTreeMaintenance<TKey, TValue> GetCoordinatedColumnMaintenance<TKey, TValue>(string name);

  /// <summary>Gets the storage maintenance of a raw column.</summary>
  /// <exception cref="KeyNotFoundException">The column does not exist.</exception>
  /// <exception cref="InvalidOperationException">The column has a different kind or types.</exception>
  IZoneTreeMaintenance<TKey, TValue> GetRawColumnMaintenance<TKey, TValue>(string name);

  /// <summary>
  /// Gets the transactional ZoneTree that stores a standalone column, for its single-tree
  /// API and maintenance. The cluster owns its lifetime.
  /// </summary>
  /// <exception cref="KeyNotFoundException">The column does not exist.</exception>
  /// <exception cref="InvalidOperationException">The column has a different kind or types.</exception>
  ITransactionalZoneTree<TKey, TValue> GetStandaloneColumnStorage<TKey, TValue>(string name);

  /// <summary>
  /// Creates a consistent cluster-wide live backup in local storage.
  /// </summary>
  ILiveBackup CreateLiveBackup(
      LocalLiveBackupOptions storage,
      ClusterLiveBackupOptions options = null);

  /// <summary>
  /// Creates a consistent cluster-wide live backup in custom object storage.
  /// </summary>
  ILiveBackup CreateLiveBackup(
      IBackupObjectStorage storage,
      ObjectStorageLiveBackupOptions storageOptions = null,
      ClusterLiveBackupOptions options = null);
}
