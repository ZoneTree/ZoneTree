namespace ZoneTree.Cluster;

/// <summary>The outcome of a transaction, as a domain or standalone column reports it.</summary>
public enum TransactionOutcome : byte
{
  /// <summary>
  /// The source cannot tell the outcome, and asking again does not change that:
  /// the transaction is not one of its own, or its outcome is no longer kept.
  /// </summary>
  Unknown = 0,

  /// <summary>
  /// The source knows the transaction, and its outcome is not decided yet.
  /// Ask again later; it becomes <see cref="Committed"/> or <see cref="Aborted"/>.
  /// </summary>
  Pending = 1,

  /// <summary>The transaction committed.</summary>
  Committed = 2,

  /// <summary>The transaction did not commit, and none of its writes are visible.</summary>
  Aborted = 3,
}
