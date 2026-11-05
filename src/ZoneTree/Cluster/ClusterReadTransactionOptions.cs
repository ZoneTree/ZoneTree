namespace ZoneTree.Cluster;

/// <summary>The options of a read-only transaction of a domain or standalone column.</summary>
public sealed class ClusterReadTransactionOptions
{
  /// <summary>
  /// The time after which a transaction that is still open is ended, so a forgotten
  /// transaction does not keep the versions its snapshot can read. Its later operations
  /// throw <see cref="ObjectDisposedException"/>. Zero, the default, or
  /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> means no timeout.
  /// </summary>
  public TimeSpan Timeout { get; init; }
}
