namespace ZoneTree.Cluster;

/// <summary>
/// The kind of a column. The values are stored in the cluster schema.
/// </summary>
internal enum ColumnKind : byte
{
  /// <summary>A cluster MVCC tree that takes part in cluster transactions.</summary>
  Coordinated = 0,

  /// <summary>A plain, non-transactional ZoneTree.</summary>
  Raw = 1,

  /// <summary>An MVCC tree whose transactions cover only this column.</summary>
  Standalone = 2,
}
