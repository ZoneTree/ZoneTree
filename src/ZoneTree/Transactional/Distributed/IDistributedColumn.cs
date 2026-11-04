namespace ZoneTree.Transactional.Distributed;

/// <summary>
/// A reusable, typed handle to a database column with its own key and value types.
/// </summary>
/// <remarks>
/// Access values through a database transaction. The database owns the column's lifetime.
/// </remarks>
public interface IDistributedColumn<TKey, TValue>
{
  /// <summary>The persisted, case-sensitive column name.</summary>
  string Name { get; }

  /// <summary>The database that owns this column.</summary>
  IDistributedDatabase Database { get; }

  /// <summary>
  /// Per-column storage maintenance and diagnostics.
  /// </summary>
  ITransactionalZoneTreeMaintenance<TKey, TValue> Maintenance { get; }

  /// <summary>Creates a maintainer for this column only.</summary>
  IMaintainer CreateMaintainer();
}
