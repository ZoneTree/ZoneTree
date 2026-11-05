using ZoneTree.Logger;

namespace ZoneTree.Cluster;

/// <summary>Startup configuration for a cluster distributed by column.</summary>
public sealed class ZoneTreeClusterOptions
{
  /// <summary>
  /// The cluster data directory.
  /// </summary>
  public string DataDirectory { get; init; } = "./cluster-data";

  /// <summary>
  /// The domains, with unique, case-sensitive names. Coordinated columns are declared in their domain.
  /// </summary>
  public IReadOnlyList<DomainDefinition> Domains { get; init; }
      = Array.Empty<DomainDefinition>();

  /// <summary>
  /// The standalone and raw column definitions. Column names are unique and case-sensitive
  /// across the cluster, including the columns of domains.
  /// </summary>
  public IReadOnlyList<ColumnDefinition> Columns { get; init; }
      = Array.Empty<ColumnDefinition>();

  /// <summary>
  /// Optional storage defaults for all columns of every kind.
  /// Each column's ConfigureStorage hook runs afterward and can override them.
  /// </summary>
  public IColumnStorageConfigurator StorageDefaults { get; init; }

  /// <summary>The logger shared by the cluster and its columns.</summary>
  public ILogger Logger { get; init; } = new ConsoleLogger();

  /// <summary>Initial admission flag for writing transactions; default is false.</summary>
  public bool IsReadOnly { get; init; }

  /// <summary>Initial background cluster WAL sync interval; zero disables it.</summary>
  public TimeSpan AutoSyncInterval { get; init; } = TimeSpan.Zero;
}
