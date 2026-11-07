namespace ZoneTree.Cluster;

/// <summary>
/// The kind of a cluster column, fixed when the column is created. The values are stored in
/// the cluster catalog.
/// </summary>
public enum ColumnKind : byte
{
  /// <summary>A column of a domain that takes part in the domain's cluster transactions.</summary>
  Coordinated = 0,

  /// <summary>A plain, non-transactional ZoneTree.</summary>
  Raw = 1,

  /// <summary>An MVCC tree whose transactions cover only this column.</summary>
  Standalone = 2,
}
