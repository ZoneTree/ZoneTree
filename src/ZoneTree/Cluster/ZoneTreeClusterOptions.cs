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
  /// The column definitions, with unique, case-sensitive names.
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
