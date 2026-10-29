using ZoneTree.Logger;
using ZoneTree.Transactional;

namespace ZoneTree;

/// <summary>
/// A ZoneTree with multi-version snapshot reads and atomic, isolated transactions.
/// </summary>
/// <remarks>
/// <para>
/// Every transaction reads a consistent snapshot taken when it begins, including
/// range reads. Reads never block and read-only transactions never conflict.
/// Writes go to the tree as the transaction makes them, so a transaction's size is
/// limited by the disk, not the memory; the memory holds only the keys it wrote and,
/// for serializable transactions, the keys and ranges it read.
/// </para>
/// <para>
/// A commit is a single record, and a crash never recovers part of a transaction.
/// A committed transaction is visible at once but not yet durable; await
/// <see cref="ITransaction{TKey, TValue}.CommitDurableAsync"/> or
/// <see cref="SyncWALAsync"/> when it must survive a power loss. Concurrent waiters
/// share disk syncs, so syncing once per batch of commits is cheaper than once per commit.
/// </para>
/// <para>This interface is thread-safe. A transaction must be used by one thread at a time.</para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface ITransactionalZoneTree<TKey, TValue> : IDisposable
{
  /// <summary>
  /// Begins a transaction that can read and write.
  /// </summary>
  /// <param name="isolationLevel">The isolation level.</param>
  /// <returns>The transaction.</returns>
  /// <exception cref="Exceptions.ZoneTreeIsReadOnlyException">The tree is read-only.</exception>
  ITransaction<TKey, TValue> BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable);

  /// <summary>
  /// Begins a transaction that only reads. It reads a consistent snapshot,
  /// never conflicts and keeps no read set.
  /// </summary>
  /// <returns>The transaction.</returns>
  ITransaction<TKey, TValue> BeginReadOnlyTransaction();

  /// <summary>
  /// Syncs the write ahead log to the disk. Every transaction committed before the
  /// call is durable when the task completes. Concurrent callers share disk syncs.
  /// </summary>
  /// <param name="cancellationToken">Stops waiting; the sync itself is not cancelled.</param>
  Task SyncWALAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// The interval of a background sync of the write ahead log. It bounds the
  /// commits a power loss can lose without syncing after every commit.
  /// <see cref="TimeSpan.Zero"/> disables the background sync. Default is zero.
  /// </summary>
  TimeSpan AutoSyncInterval { get; set; }

  /// <summary>
  /// Gets or sets the read-only flag. Writing transactions cannot begin while it is set.
  /// </summary>
  bool IsReadOnly { get; set; }

  /// <summary>
  /// The logger.
  /// </summary>
  ILogger Logger { get; }

  /// <summary>
  /// The maintenance view of the transactional tree.
  /// </summary>
  ITransactionalZoneTreeMaintenance<TKey, TValue> Maintenance { get; }

  /// <summary>
  /// Creates a maintainer that merges the storage tree in the background.
  /// </summary>
  /// <returns>The maintainer.</returns>
  IMaintainer CreateMaintainer();
}
