using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>
/// A reusable, typed handle to a cluster column with its own key and value types.
/// </summary>
/// <remarks>
/// Access values through a cluster transaction. The cluster owns the column's lifetime.
/// </remarks>
public interface IColumn<TKey, TValue>
{
  /// <summary>The persisted, case-sensitive column name.</summary>
  string Name { get; }

  /// <summary>The cluster that owns this column.</summary>
  IZoneTreeCluster Cluster { get; }

  /// <summary>
  /// Per-column storage maintenance and diagnostics.
  /// </summary>
  ITransactionalZoneTreeMaintenance<TKey, TValue> Maintenance { get; }

  /// <summary>Creates a maintainer for this column only.</summary>
  IMaintainer CreateMaintainer();
}
