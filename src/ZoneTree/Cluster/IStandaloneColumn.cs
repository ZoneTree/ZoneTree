namespace ZoneTree.Cluster;

/// <summary>
/// A column whose transactions cover only this column, so its handles cannot be passed
/// to domain transactions. It is backed by the MVCC engine of a transactional ZoneTree.
/// </summary>
/// <remarks>The cluster owns the column's lifetime.</remarks>
public interface IStandaloneColumn<TKey, TValue> : IClusterTransactionSource<IStandaloneTransaction<TKey, TValue>>
{
  /// <summary>The persisted, case-sensitive column name.</summary>
  string Name { get; }

  /// <summary>The cluster that owns this column.</summary>
  IZoneTreeCluster Cluster { get; }

  /// <summary>Begins a read-only transaction of this column.</summary>
  IStandaloneReadTransaction<TKey, TValue> BeginReadOnlyTransaction();

  /// <summary>Asynchronously begins a read-only transaction of this column.</summary>
  ValueTask<IStandaloneReadTransaction<TKey, TValue>> BeginReadOnlyTransactionAsync(
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Makes the column's transactions committed before this call durable across power loss or an OS crash.
  /// </summary>
  Task SyncWALAsync(CancellationToken cancellationToken = default);
}
