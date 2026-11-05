namespace ZoneTree.Cluster;

/// <summary>
/// A column backed by the MVCC engine of a transactional ZoneTree. Its transactions cover
/// only this column, so its handles cannot be passed to cluster transactions.
/// </summary>
/// <remarks>
/// Use it like any transactional ZoneTree, including its extension helpers. The cluster
/// owns the column's lifetime: disposing a handle does nothing.
/// </remarks>
public interface IStandaloneColumn<TKey, TValue> : ITransactionalZoneTree<TKey, TValue>
{
  /// <summary>The persisted, case-sensitive column name.</summary>
  string Name { get; }

  /// <summary>The cluster that owns this column.</summary>
  IZoneTreeCluster Cluster { get; }
}
