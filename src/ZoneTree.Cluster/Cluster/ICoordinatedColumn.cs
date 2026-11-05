
namespace ZoneTree.Cluster;

/// <summary>
/// A reusable, typed handle to a cluster column that takes part in cluster transactions,
/// with its own key and value types.
/// </summary>
/// <remarks>
/// Access values through a cluster transaction. The cluster owns the column's lifetime.
/// </remarks>
public interface ICoordinatedColumn<TKey, TValue>
{
  /// <summary>The persisted, case-sensitive column name.</summary>
  string Name { get; }

  /// <summary>The cluster that owns this column.</summary>
  IZoneTreeCluster Cluster { get; }

  /// <summary>The domain whose transactions include this column.</summary>
  IClusterDomain Domain { get; }
}
