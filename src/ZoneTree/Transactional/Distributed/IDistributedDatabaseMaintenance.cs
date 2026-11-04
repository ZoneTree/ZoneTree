namespace ZoneTree.Transactional.Distributed;

/// <summary>Database-wide maintenance; individual storage trees belong to columns.</summary>
public interface IDistributedDatabaseMaintenance
{
  /// <summary>The column names in schema declaration order.</summary>
  IReadOnlyList<string> ColumnNames { get; }

  /// <summary>The number of active database transactions, each counted once.</summary>
  int ActiveTransactionCount { get; }

  /// <summary>
  /// The timestamp of the latest committed database transaction.
  /// </summary>
  long LastCommitTimestamp { get; }

  /// <summary>The number of aborted database transactions still registered for cleanup.</summary>
  int AbortedTransactionCount { get; }

  /// <summary>
  /// The total number of conflict-index entries across columns.
  /// </summary>
  int ConflictIndexCount { get; }

  /// <summary>Saves database metadata.</summary>
  void SaveMetaData();

  /// <summary>
  /// Disposes the database and deletes its files.
  /// </summary>
  void Drop();
}
