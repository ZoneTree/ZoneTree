namespace ZoneTree.Cluster;

/// <summary>The outcome of a transaction, as a domain or standalone column reports it.</summary>
public enum TransactionOutcome : byte
{
  /// <summary>The outcome is not known, or not decided yet.</summary>
  Unknown = 0,

  /// <summary>The transaction committed.</summary>
  Committed = 1,

  /// <summary>The transaction did not commit.</summary>
  Aborted = 2,
}
