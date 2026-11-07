namespace ZoneTree.Exceptions;

/// <summary>
/// Thrown when the commit outcome of a transaction is unknown: the write ahead log sync of
/// its commit failed, or a cluster transaction failed after commits of its columns started.
/// The transaction cannot be retried or rolled back. Recovery after the tree or cluster
/// is reopened decides what committed.
/// </summary>
public sealed class TransactionInDoubtException : ZoneTreeException
{
  public TransactionInDoubtException(long transactionId, Exception innerException = null)
      : base($"Transaction's commit outcome is unknown. Transaction Id: {transactionId}", innerException)
  {
    TransactionId = transactionId;
  }

  public long TransactionId { get; }
}
