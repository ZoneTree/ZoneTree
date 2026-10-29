namespace ZoneTree.Exceptions;

public sealed class TransactionIsReadOnlyException : ZoneTreeException
{
  public TransactionIsReadOnlyException(long transactionId)
      : base($"Transaction is read-only. Transaction Id: {transactionId}")
  {
    TransactionId = transactionId;
  }

  public long TransactionId { get; }
}
