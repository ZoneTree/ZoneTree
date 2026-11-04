namespace ZoneTree.Transactional.Distributed;

/// <summary>
/// A reusable, typed handle to one column owned by a distributed database.
/// Each column may have different key and value types, comparers and serializers.
/// Equal keys in different columns are independent; no implicit row join is performed.
/// </summary>
/// <remarks>
/// This handle does not open a transaction or pin a snapshot. Access values through
/// a database transaction. The handle remains valid until its database is disposed.
/// The database owns the underlying tree and its lifetime.
/// </remarks>
public interface IDistributedColumn<TKey, TValue>
{
  /// <summary>The persisted, case-sensitive column name.</summary>
  string Name { get; }

  /// <summary>The database that owns this column.</summary>
  IDistributedDatabase Database { get; }

  /// <summary>
  /// Per-column storage maintenance and diagnostics. Transaction timestamps use
  /// the database clock. Raw storage mutations, Drop and separate transactional
  /// access bypass database coordination and are unsupported while it is open.
  /// Use database maintenance to drop the database.
  /// </summary>
  ITransactionalZoneTreeMaintenance<TKey, TValue> Maintenance { get; }

  /// <summary>Creates a maintainer for this column only.</summary>
  IMaintainer CreateMaintainer();
}
