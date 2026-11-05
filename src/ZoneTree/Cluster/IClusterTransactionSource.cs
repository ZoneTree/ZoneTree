using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>
/// Begins writing transactions: a domain, or a standalone column.
/// The cluster transaction helpers, such as Execute, work with any source.
/// </summary>
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

  /// <summary>
  /// Finds the outcome of a transaction of this source, for example after its commit
  /// ended in doubt. <see cref="TransactionOutcome.Pending"/> means the outcome is not
  /// decided yet, so ask again; <see cref="TransactionOutcome.Unknown"/> means this source
  /// cannot tell it.
  /// </summary>
  ValueTask<TransactionOutcome> GetTransactionOutcomeAsync(
      long transactionId, CancellationToken cancellationToken = default);
}
