using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>
/// Begins writing transactions: a domain, or a standalone column.
/// The cluster transaction helpers, such as Execute, work with any source.
/// </summary>
/// <remarks>
/// The asynchronous begin methods return a <see cref="ValueTask{TResult}"/>, unlike the
/// other asynchronous methods of the cluster: a cluster in this process begins every
/// transaction synchronously, so the begin allocates no task.
/// </remarks>
public interface IClusterTransactionSource<TTransaction>
    where TTransaction : IClusterWriteTransaction
{
  /// <summary>Begins a read/write transaction with the requested isolation level.</summary>
  /// <exception cref="Exceptions.ZoneTreeIsReadOnlyException">The cluster is read-only.</exception>
  TTransaction BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable);

  /// <summary>Asynchronously begins a read/write transaction.</summary>
  ValueTask<TTransaction> BeginTransactionAsync(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      CancellationToken cancellationToken = default);

  /// <summary>Begins a read/write transaction with the given options; null uses the defaults.</summary>
  /// <exception cref="Exceptions.ZoneTreeIsReadOnlyException">The cluster is read-only.</exception>
  TTransaction BeginTransaction(ClusterTransactionOptions options);

  /// <summary>Asynchronously begins a read/write transaction with the given options.</summary>
  ValueTask<TTransaction> BeginTransactionAsync(
      ClusterTransactionOptions options, CancellationToken cancellationToken = default);

  /// <summary>
  /// Finds the outcome of a transaction of this source, for example after its commit
  /// ended in doubt. <see cref="TransactionOutcome.Pending"/> means the outcome is not
  /// decided yet, so ask again; <see cref="TransactionOutcome.Unknown"/> means this source
  /// cannot tell it.
  /// </summary>
  /// <remarks>
  /// A domain asks the columns that are open in this process. A transaction that wrote only
  /// columns this process has not opened yet reports <see cref="TransactionOutcome.Unknown"/>;
  /// open them first, with <c>GetColumn</c>, to find its outcome.
  /// </remarks>
  Task<TransactionOutcome> GetTransactionOutcomeAsync(
      long transactionId, CancellationToken cancellationToken = default);
}
