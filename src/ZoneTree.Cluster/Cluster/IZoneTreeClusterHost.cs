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
  /// <exception cref="SchemaObjectNotFoundException">The column does not exist.</exception>
  IMaintainer CreateColumnMaintainer(string name);

  /// <summary>Gets the storage maintenance of a coordinated column.</summary>
  /// <exception cref="SchemaObjectNotFoundException">The column does not exist.</exception>
  /// <exception cref="SchemaConflictException">The column has a different kind or types.</exception>
  ITransactionalZoneTreeMaintenance<TKey, TValue> GetCoordinatedColumnMaintenance<TKey, TValue>(string name);

  /// <summary>Gets the storage maintenance of a raw column.</summary>
  /// <exception cref="SchemaObjectNotFoundException">The column does not exist.</exception>
  /// <exception cref="SchemaConflictException">The column has a different kind or types.</exception>
  IZoneTreeMaintenance<TKey, TValue> GetRawColumnMaintenance<TKey, TValue>(string name);

  /// <summary>
  /// Gets the transactional ZoneTree that stores a standalone column, for its single-tree
  /// API and maintenance. The cluster owns its lifetime.
  /// </summary>
  /// <exception cref="SchemaObjectNotFoundException">The column does not exist.</exception>
  /// <exception cref="SchemaConflictException">The column has a different kind or types.</exception>
  ITransactionalZoneTree<TKey, TValue> GetStandaloneColumnStorage<TKey, TValue>(string name);

  /// <summary>
  /// Creates a consistent cluster-wide live backup in local storage.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A generation backs up the catalog and every column while the cluster serves reads and
  /// writes. Each domain's part is one cut: the domain's snapshot when the generation began.
  /// Every column of the domain holds exactly the commits of that cut after a restore. Raw and
  /// standalone columns are each backed up at one moment of their own.
  /// </para>
  /// <para>
  /// Every column must be open in this process: declared, or opened with <c>GetColumn</c>.
  /// Each column keeps its generations in a store of its own under the backup location, and a
  /// generation publishes once every column uploaded. Column drops wait for a running
  /// generation. <see cref="LocalLiveBackupOptions.KeepLastGenerations"/> keeps that many
  /// cluster generations.
  /// </para>
  /// </remarks>
  ILiveBackup CreateLiveBackup(
      LocalLiveBackupOptions storage,
      ClusterLiveBackupOptions options = null);

  /// <summary>
  /// Creates a consistent cluster-wide live backup in custom object storage.
  /// </summary>
  /// <remarks>
  /// The generations work as in local storage. The objects live under
  /// <see cref="ObjectStorageLiveBackupOptions.Prefix"/>, which must belong to this cluster.
  /// </remarks>
  ILiveBackup CreateLiveBackup(
      IBackupObjectStorage storage,
      ObjectStorageLiveBackupOptions storageOptions = null,
      ClusterLiveBackupOptions options = null);
}
