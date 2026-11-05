namespace ZoneTree.Exceptions;

/// <summary>
/// Thrown when a transaction is used while its commit waits for a write ahead log sync
/// before it becomes visible. The commit continues; await the commit to see its outcome.
/// </summary>
public sealed class TransactionCommitInProgressException : ZoneTreeException
{
  public TransactionCommitInProgressException(long transactionId)
      : base($"Transaction is waiting for its commit to be published. Transaction Id: {transactionId}")
  {
    TransactionId = transactionId;
  }

  public long TransactionId { get; }
}
