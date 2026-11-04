using ZoneTree.Logger;

namespace ZoneTree.Transactional.Distributed;

/// <summary>Startup configuration for a database distributed by column.</summary>
/// <remarks>
/// The factory validates and snapshots configuration before opening storage.
/// Names and column membership are persisted; reopening validates the complete
/// schema, types, comparers and serializers. No columns are silently added or removed.
/// This first API supports a fixed schema and in-process placement only.
/// </remarks>
public sealed class DistributedDatabaseOptions
{
  /// <summary>
  /// The database root, containing the schema, transaction coordinator and separate
  /// column directories. A database exclusively owns its root while open.
  /// Column names are logical names; they are not interpreted as filesystem paths.
  /// </summary>
  public string DataDirectory { get; init; } = "./distributed-data";

  /// <summary>
  /// The nonempty, complete schema, with unique, case-sensitive column names.
  /// Each typed definition creates one separately stored ZoneTree column.
  /// The factory copies this list; do not mutate its contents during opening.
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
