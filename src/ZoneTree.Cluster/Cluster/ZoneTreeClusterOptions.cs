using ZoneTree.Logger;

namespace ZoneTree.Cluster;

/// <summary>Startup configuration for a cluster distributed by column.</summary>
/// <remarks>
/// The cluster catalog is the source of truth for the schema. The declared domains and
/// columns are created when they are missing and checked when they exist; declaration order
/// does not matter, and the catalog may hold more domains and columns than are declared.
/// </remarks>
public sealed class ZoneTreeClusterOptions
{
  /// <summary>
  /// The cluster data directory.
  /// </summary>
  public string DataDirectory { get; init; } = "./cluster-data";

  /// <summary>
  /// The declared domains, with unique, case-sensitive names. Coordinated columns are declared in their domain.
  /// </summary>
  public IReadOnlyList<DomainDefinition> Domains { get; init; }
      = Array.Empty<DomainDefinition>();

  /// <summary>
  /// The declared standalone and raw columns. Column names are unique and case-sensitive
  /// across the cluster, including the columns of domains.
  /// </summary>
  public IReadOnlyList<ColumnDefinition> Columns { get; init; }
      = Array.Empty<ColumnDefinition>();

  /// <summary>
  /// The serializers and comparers that column definitions name by id, including the
  /// built-in ones. Register custom encodings before opening the cluster.
  /// </summary>
  public ClusterEncodingRegistry Encodings { get; init; } = new();

  /// <summary>
  /// Optional storage defaults for all columns of every kind, including columns created at
  /// run time. Each column's ConfigureStorage hook runs afterward and can override them.
  /// </summary>
  public IColumnStorageConfigurator StorageDefaults { get; init; }

  /// <summary>The logger shared by the cluster and its columns.</summary>
  public ILogger Logger { get; init; } = new ConsoleLogger();

  /// <summary>
  /// Initial admission flag for writing transactions, raw writes and schema changes; default
  /// is false. The declared schema is applied before it takes effect.
  /// </summary>
  public bool IsReadOnly { get; init; }

  /// <summary>Initial background cluster WAL sync interval; zero disables it.</summary>
  public TimeSpan AutoSyncInterval { get; init; } = TimeSpan.Zero;
}
