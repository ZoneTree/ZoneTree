using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>The options of a writing transaction of a domain or standalone column.</summary>
public sealed class ClusterTransactionOptions
{
  /// <summary>The isolation level, applied across all accessed columns.</summary>
  public TransactionIsolationLevel IsolationLevel { get; init; } = TransactionIsolationLevel.Serializable;

  /// <summary>
  /// The time after which a transaction that is still active is rolled back, so a
  /// forgotten transaction does not keep the versions its snapshot can read. Its later
  /// operations, and its commit, throw <see cref="Exceptions.TransactionAbortedException"/>.
  /// A commit that started first is not affected. Zero, the default, or
  /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> means no timeout.
  /// </summary>
  public TimeSpan Timeout { get; init; }
}
