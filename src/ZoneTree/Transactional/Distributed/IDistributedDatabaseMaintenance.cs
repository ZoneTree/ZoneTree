namespace ZoneTree.Transactional.Distributed;

/// <summary>Database-wide maintenance; individual storage trees belong to columns.</summary>
public interface IDistributedDatabaseMaintenance
{
  /// <summary>A cached, immutable list of column names in schema declaration order.</summary>
  IReadOnlyList<string> ColumnNames { get; }

  /// <summary>The number of active database transactions, each counted once.</summary>
  int ActiveTransactionCount { get; }

  /// <summary>
  /// The latest published database commit timestamp. New transactions use this
  /// snapshot. Prepared durable commits do not advance it before their sync succeeds.
  /// </summary>
  long LastCommitTimestamp { get; }

  /// <summary>The number of aborted database transactions still registered for cleanup.</summary>
  int AbortedTransactionCount { get; }

  /// <summary>
  /// The sum of column conflict-index entries. The same key in two columns counts
  /// twice. Reading this diagnostic may visit all columns; it is not a hot-path counter.
  /// </summary>
  int ConflictIndexCount { get; }

  /// <summary>Saves schema, coordinator and column metadata; this is not a WAL sync.</summary>
  void SaveMetaData();

  /// <summary>
  /// Disposes the database and deletes its coordinator and all column files.
  /// Requires no active transactions, maintainers or backups. Individual columns
  /// cannot be dropped independently in the initial, fixed-schema implementation.
  /// </summary>
  void Drop();
}
