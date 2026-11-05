using ZoneTree.Transactional;
using ZoneTree.Backup;
using ZoneTree.Logger;

namespace ZoneTree.Cluster;

/// <summary>
/// A cluster with independently typed coordinated, standalone and raw columns.
/// </summary>
/// <remarks>
/// Coordinated columns belong to domains, and cluster transactions begin from a domain.
/// Standalone columns have their own transactions, and raw columns are accessed directly.
/// The cluster, its domains and its column handles are thread-safe.
/// Disposing or dropping the cluster rolls back its active transactions.
/// </remarks>
public interface IZoneTreeCluster : IDisposable, IAsyncDisposable
{
  /// <summary>Gets a domain of coordinated columns. Names are case-sensitive.</summary>
  /// <exception cref="KeyNotFoundException">The domain does not exist.</exception>
  IClusterDomain GetDomain(string name);

  /// <summary>Gets a typed handle to a column with its own transactions. Names are case-sensitive.</summary>
  /// <exception cref="KeyNotFoundException">The column does not exist.</exception>
  /// <exception cref="InvalidOperationException">The column has a different kind or types.</exception>
  IStandaloneColumn<TKey, TValue> GetStandaloneColumn<TKey, TValue>(string name);

  /// <summary>Gets a typed handle to a non-transactional column. Names are case-sensitive.</summary>
  IRawColumn<TKey, TValue> GetRawColumn<TKey, TValue>(string name);

  /// <summary>
  /// Makes completed raw writes and transactions committed before this call durable across power loss or an OS crash.
  /// </summary>
  Task SyncWALAsync(CancellationToken cancellationToken = default);

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
  /// Creates a maintainer for the cluster.
  /// </summary>
  IMaintainer CreateMaintainer();

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
