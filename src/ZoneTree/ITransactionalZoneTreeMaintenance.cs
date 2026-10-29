using ZoneTree.Transactional;

namespace ZoneTree;

/// <summary>
/// The maintenance view of a transactional ZoneTree.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface ITransactionalZoneTreeMaintenance<TKey, TValue>
{
  /// <summary>
  /// The storage tree. Every user key is stored with the id of the transaction that
  /// wrote it. Writing to the storage tree directly bypasses the transactions.
  /// </summary>
  IZoneTree<VersionedKey<TKey>, VersionedValue<TValue>> ZoneTree { get; }

  /// <summary>
  /// The number of transactions that have not ended.
  /// </summary>
  int ActiveTransactionCount { get; }

  /// <summary>
  /// The commit timestamp of the latest committed transaction.
  /// New transactions read the snapshot of this timestamp.
  /// </summary>
  long LastCommitTimestamp { get; }

  /// <summary>
  /// The number of rolled back transactions whose versions may still be stored.
  /// Reads skip these versions and merges drop them.
  /// </summary>
  int AbortedTransactionCount { get; }

  /// <summary>
  /// The number of keys kept to detect conflicts. Keys written by a commit stay until
  /// every writing transaction that began before the commit has ended.
  /// Read-only transactions do not keep them.
  /// </summary>
  int ConflictIndexCount { get; }

  /// <summary>
  /// Saves the storage tree metadata.
  /// </summary>
  void SaveMetaData();

  /// <summary>
  /// Disposes the tree and deletes its files.
  /// </summary>
  void Drop();
}
