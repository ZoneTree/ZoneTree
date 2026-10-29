namespace ZoneTree.Transactional;

/// <summary>
/// The states of a transaction.
/// </summary>
public enum TransactionState : byte
{
  /// <summary>
  /// The transaction can read and write.
  /// </summary>
  Active = 0,

  /// <summary>
  /// The transaction is committed.
  /// </summary>
  Committed = 1,

  /// <summary>
  /// The transaction is rolled back.
  /// </summary>
  Aborted = 2,
}
