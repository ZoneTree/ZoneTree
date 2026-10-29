namespace ZoneTree.Exceptions;

/// <summary>
/// Thrown when a transaction conflicts with a transaction that committed after
/// its snapshot. The transaction is rolled back and can be retried from the start.
/// </summary>
public sealed class TransactionConflictException : ZoneTreeException
{
  public TransactionConflictException(long transactionId)
      : base($"Transaction conflicts with a committed transaction and is rolled back. Transaction Id: {transactionId}")
  {
    TransactionId = transactionId;
  }

  public long TransactionId { get; }
}
