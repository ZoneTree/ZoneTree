namespace ZoneTree.Exceptions;

/// <summary>
/// Thrown when a transaction is used after the write ahead log sync of its commit failed.
/// The commit stays invisible in this instance, and it cannot be retried or rolled back.
/// Recovery after the tree is reopened decides whether it committed.
/// </summary>
public sealed class TransactionInDoubtException : ZoneTreeException
{
  public TransactionInDoubtException(long transactionId)
      : base($"Transaction's commit outcome is unknown after its commit sync failed. Transaction Id: {transactionId}")
  {
    TransactionId = transactionId;
  }

  public long TransactionId { get; }
}
