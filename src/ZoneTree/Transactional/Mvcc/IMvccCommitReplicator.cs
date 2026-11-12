namespace ZoneTree.Transactional.Mvcc;

/// <summary>
/// Replicates the commits of an MVCC tree whose replicas another log keeps, such as the log of a
/// replica group. The tree captures what each commit wrote before its commit point, and starts its
/// replication at the commit point, in commit order. The tree writes no commit point of its own: the
/// replication writes it, deleting the transaction's status record, on every replica once the
/// replicas hold the commit, and the tree publishes the commit after that. A replication that fails
/// leaves its commit in doubt and stops the tree's commits, as a failed sync does.
/// </summary>
internal interface IMvccCommitReplicator<TKey>
{
  /// <summary>Reads what a commit wrote, its versions of the keys, before its commit point.</summary>
  object Capture(long transactionId, TKey[] keys);

  /// <summary>
  /// Starts replicating a captured commit at its commit point; the task completes once its replicas
  /// hold it and this tree holds its commit point.
  /// </summary>
  Task Replicate(object captured, long commitTimestamp);

  /// <summary>Returns once the commits replicated before the call are as durable as the replicas make them, for a durable commit or a sync.</summary>
  Task SyncAsync();
}
