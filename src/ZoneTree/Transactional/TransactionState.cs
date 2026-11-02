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

  /// <summary>The commit record was appended and is waiting for publication.</summary>
  Committing = 3,

  /// <summary>
  /// A commit sync failed. The transaction stays invisible in this instance,
  /// but recovery may find its commit record. It cannot be reused or rolled back.
  /// </summary>
  InDoubt = 4,
}
