using ZoneTree.Logger;

namespace ZoneTree.Transactional.Distributed;

/// <summary>Startup configuration for a database distributed by column.</summary>
public sealed class DistributedDatabaseOptions
{
  /// <summary>
  /// The database data directory.
  /// </summary>
  public string DataDirectory { get; init; } = "./distributed-data";

  /// <summary>
  /// The column definitions, with unique, case-sensitive names.
  /// </summary>
  public IReadOnlyList<DistributedColumnDefinition> Columns { get; init; }
      = Array.Empty<DistributedColumnDefinition>();

  /// <summary>The logger shared by the database and its columns.</summary>
  public ILogger Logger { get; init; } = new ConsoleLogger();

  /// <summary>Initial admission flag for writing transactions; default is false.</summary>
  public bool IsReadOnly { get; init; }

  /// <summary>Initial background database WAL sync interval; zero disables it.</summary>
  public TimeSpan AutoSyncInterval { get; init; } = TimeSpan.Zero;
}
