using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>
/// A group of coordinated columns with one timestamp source and one commit protocol.
/// Cluster transactions span the columns of a single domain.
/// </summary>
/// <remarks>The domain and its column handles are thread-safe.</remarks>
public interface IClusterDomain : IClusterTransactionSource<IDomainTransaction>
{
  /// <summary>The persisted, case-sensitive domain name.</summary>
  string Name { get; }

  /// <summary>The cluster that owns this domain.</summary>
  IZoneTreeCluster Cluster { get; }

  /// <summary>The domain's column names in creation order, including columns created at run time.</summary>
  IReadOnlyList<string> ColumnNames { get; }

  /// <summary>Gets a typed handle to a column of this domain. Names are case-sensitive.</summary>
  /// <exception cref="KeyNotFoundException">The domain has no such column.</exception>
  /// <exception cref="InvalidOperationException">The column has different types.</exception>
  ICoordinatedColumn<TKey, TValue> GetColumn<TKey, TValue>(string name);

  /// <summary>Begins a read-only transaction across the domain's columns.</summary>
  IDomainReadTransaction BeginReadOnlyTransaction();

  /// <summary>Asynchronously begins a read-only transaction.</summary>
  ValueTask<IDomainReadTransaction> BeginReadOnlyTransactionAsync(
      CancellationToken cancellationToken = default);

  /// <summary>Begins a read-only transaction with the given options; null uses the defaults.</summary>
  IDomainReadTransaction BeginReadOnlyTransaction(ClusterReadTransactionOptions options);

  /// <summary>Asynchronously begins a read-only transaction with the given options.</summary>
  ValueTask<IDomainReadTransaction> BeginReadOnlyTransactionAsync(
      ClusterReadTransactionOptions options, CancellationToken cancellationToken = default);

  /// <summary>The timestamp of the domain's latest committed transaction.</summary>
  long LastCommitTimestamp { get; }

  /// <summary>
  /// Makes the domain's transactions committed before this call durable across power loss or an OS crash.
  /// </summary>
  Task SyncWALAsync(CancellationToken cancellationToken = default);
}
