using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>Rolls back a transaction that is still active when its timeout elapses.</summary>
internal static class TransactionExpiry
{
  /// <summary>Validates the options before a transaction begins.</summary>
  internal static void Validate(ClusterTransactionOptions options)
  {
    if (options.IsolationLevel is not TransactionIsolationLevel.Snapshot
        and not TransactionIsolationLevel.Serializable)
      throw new ArgumentOutOfRangeException(nameof(options), options.IsolationLevel, "Unsupported isolation level.");
    ValidateTimeout(options.Timeout);
  }

  /// <summary>Validates the options before a read-only transaction begins.</summary>
  internal static void Validate(ClusterReadTransactionOptions options) => ValidateTimeout(options.Timeout);

  static void ValidateTimeout(TimeSpan timeout)
  {
    if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
      throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The transaction timeout cannot be negative.");
  }

  /// <summary>
  /// Starts the timer of a transaction with a timeout, or returns null. The cluster
  /// tracks the open transaction, which keeps the timer alive until it ends.
  /// </summary>
  internal static Timer Start(TimeSpan timeout, Action expire)
  {
    if (timeout == TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
      return null;
    return new Timer(static state => ((Action)state)(), expire, timeout, Timeout.InfiniteTimeSpan);
  }
}
