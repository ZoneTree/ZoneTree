using ZoneTree.Backup;
using ZoneTree.Exceptions;

namespace ZoneTree.Cluster;

/// <summary>Creates and opens clusters whose columns run in this process.</summary>
/// <remarks>
/// A cluster exists once its catalog exists. Creating a cluster creates the catalog; opening
/// one applies the declared schema to the catalog it finds.
/// </remarks>
public sealed class ZoneTreeClusterFactory : IZoneTreeClusterFactory
{
  public IZoneTreeClusterHost CreateInProcess(ZoneTreeClusterOptions options) => Open(options, true, false);
  public IZoneTreeClusterHost OpenInProcess(ZoneTreeClusterOptions options) => Open(options, false, true);
  public IZoneTreeClusterHost OpenOrCreateInProcess(ZoneTreeClusterOptions options) => Open(options, true, true);

  static ZoneTreeCluster Open(ZoneTreeClusterOptions options, bool canCreate, bool canOpen)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(options.Logger);
    ArgumentNullException.ThrowIfNull(options.Encodings);
    if (string.IsNullOrWhiteSpace(options.DataDirectory))
      throw new ArgumentException("A data directory is required.", nameof(options));
    ZoneTreeCluster.ValidateSyncInterval(options.AutoSyncInterval);
    var columns = ValidateDeclarations(options);
    var directory = Path.GetFullPath(options.DataDirectory);
    if (!canCreate && !Directory.Exists(directory))
      throw new DatabaseNotFoundException();
    Directory.CreateDirectory(directory);
    var lease = new FileStream(Path.Combine(directory, "database.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    ClusterCatalog catalog = null;
    ZoneTreeCluster cluster = null;
    try
    {
      catalog = ClusterCatalog.Open(directory, options.Logger, canCreate, canOpen);
      cluster = new ZoneTreeCluster(directory, options, lease, catalog);
      cluster.ApplyDeclarations(options, columns);
      return cluster;
    }
    catch
    {
      try
      {
        if (cluster != null)
          cluster.Dispose();
        else
        {
          catalog?.Dispose();
          lease.Dispose();
        }
      }
      catch (Exception error)
      {
        options.Logger.LogError(error);
      }
      throw;
    }
  }

  /// <summary>
  /// Validates the declared schema before anything is created: unique names, coordinated
  /// columns only in domains, and encodings that resolve for the declared types. Lists every
  /// declared column with its domain name, which is null outside domains.
  /// </summary>
  static List<(ColumnDefinition Column, string Domain)> ValidateDeclarations(ZoneTreeClusterOptions options)
  {
    var columns = new List<(ColumnDefinition Column, string Domain)>();
    var domains = new HashSet<string>(StringComparer.Ordinal);
    foreach (var domain in options.Domains ?? [])
    {
      if (domain == null || !domains.Add(domain.Name))
        throw new ArgumentException("Domain definitions must be non-null and have unique names.", nameof(options));
      foreach (var column in domain.Columns ?? [])
        columns.Add((column, domain.Name));
    }
    foreach (var column in options.Columns ?? [])
    {
      if (column?.Kind == ColumnKind.Coordinated)
        throw new ArgumentException($"Coordinated column '{column.Name}' must be declared in a domain.", nameof(options));
      columns.Add((column, null));
    }
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (column, _) in columns)
    {
      if (column == null || !names.Add(column.Name))
        throw new ArgumentException("Column definitions must be non-null and have unique names.", nameof(options));
      column.ValidateEncodings(options.Encodings);
    }
    return columns;
  }

  public ValueTask<IZoneTreeClusterHost> RestoreInProcessAsync(ZoneTreeClusterOptions options,
      LocalLiveBackupOptions backup, long? generationId = null, CancellationToken cancellationToken = default)
      => throw new NotSupportedException("Cluster-wide backup and restore are not supported yet.");

  public ValueTask<IZoneTreeClusterHost> RestoreInProcessAsync(ZoneTreeClusterOptions options,
      IBackupObjectStorage storage, ObjectStorageLiveBackupOptions backupOptions = null,
      long? generationId = null, CancellationToken cancellationToken = default)
      => throw new NotSupportedException("Cluster-wide backup and restore are not supported yet.");

  public ValueTask<IZoneTreeCluster> ConnectAsync(ZoneTreeClusterConnectionOptions options,
      CancellationToken cancellationToken = default)
      => throw new NotSupportedException("Connecting to clusters hosted in other processes is not supported yet.");
}
