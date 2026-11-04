namespace ZoneTree.Cluster;

/// <summary>Cluster-wide maintenance; individual storage trees belong to columns.</summary>
public interface IZoneTreeClusterMaintenance
{
  /// <summary>The column names in schema declaration order.</summary>
  IReadOnlyList<string> ColumnNames { get; }

  /// <summary>The number of active cluster transactions, each counted once.</summary>
  int ActiveTransactionCount { get; }

  /// <summary>
  /// The timestamp of the latest committed cluster transaction.
  /// </summary>
  long LastCommitTimestamp { get; }

  /// <summary>The number of aborted cluster transactions still registered for cleanup.</summary>
  int AbortedTransactionCount { get; }

  /// <summary>
  /// The total number of conflict-index entries across columns.
  /// </summary>
  int ConflictIndexCount { get; }

  /// <summary>Saves cluster metadata.</summary>
  void SaveMetaData();

  /// <summary>
  /// Disposes the cluster and deletes its files.
  /// </summary>
  void Drop();
}
