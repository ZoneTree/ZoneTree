namespace ZoneTree.Transactional;

/// <summary>
/// The isolation levels of transactions.
/// </summary>
public enum TransactionIsolationLevel : byte
{
  /// <summary>
  /// The transaction reads a consistent snapshot. It conflicts with transactions
  /// that commit a write to the same key after it began (first committer wins).
  /// Write skew is possible: two transactions can each read what the other writes.
  /// </summary>
  Snapshot = 0,

  /// <summary>
  /// The transaction also conflicts when a transaction that committed after it began
  /// wrote a key it read or a key inside a range it iterated, including keys that
  /// did not exist. Committed transactions are equivalent to running one at a time
  /// in commit order.
  /// </summary>
  Serializable = 1,
}
