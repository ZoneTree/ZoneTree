using ZoneTree.Backup;
using ZoneTree.Logger;

namespace ZoneTree.Transactional.Distributed;

/// <summary>
/// One transactional database whose independently typed columns each use a ZoneTree.
/// </summary>
/// <remarks>
/// This is the proposed database contract; the distributed runtime is not implemented yet.
/// Transactions share one snapshot and commit atomically across all accessed columns.
/// The database and its column handles are thread-safe. Transactions and their column
/// views must be used by one caller at a time. Dispose transactions before the database.
/// Column membership is fixed while the database is open.
/// </remarks>
public interface IDistributedDatabase : IDisposable, IAsyncDisposable
{
  /// <summary>
  /// Resolves an existing column and checks its key and value types. Resolve once
  /// and reuse the handle; transactions do not look up column names per record.
  /// Names are case-sensitive, compared using ordinal comparison.
  /// </summary>
  /// <exception cref="KeyNotFoundException">The column does not exist.</exception>
  /// <exception cref="InvalidOperationException">The column has different types.</exception>
  IDistributedColumn<TKey, TValue> GetColumn<TKey, TValue>(string name);

  /// <summary>
  /// Begins a database transaction. All columns, including ones first accessed
  /// later, read the same database snapshot plus this transaction's own writes.
  /// Serializable isolation validates point reads, missing keys and iterated ranges
  /// across columns when the transaction writes. Snapshot isolation validates writes
  /// and keys read for update, and permits write skew as in a single MVCC tree.
  /// </summary>
  /// <exception cref="Exceptions.ZoneTreeIsReadOnlyException">The database is read-only.</exception>
  IDistributedTransaction BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable);

  /// <summary>
  /// Begins a consistent database snapshot with no read set and no conflicts.
  /// The snapshot protects versions even in columns not yet accessed.
  /// </summary>
  IDistributedReadTransaction BeginReadOnlyTransaction();

  /// <summary>
  /// Makes every transaction published before this call durable across power loss
  /// or an OS crash, including its column data and the database commit decision.
  /// Concurrent callers share sync work. Cancellation stops waiting, not the sync.
  /// </summary>
  Task SyncWALAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// The interval for background database WAL syncs; zero disables them.
  /// This is not a persistence deadline. Ordinary commits are visible before sync.
  /// </summary>
  TimeSpan AutoSyncInterval { get; set; }

  /// <summary>
  /// Prevents new writing transactions when set. Existing transactions retain
  /// their access, matching the single-tree admission rule.
  /// </summary>
  bool IsReadOnly { get; set; }

  /// <summary>The database logger.</summary>
  ILogger Logger { get; }

  /// <summary>Database-wide maintenance and diagnostics.</summary>
  IDistributedDatabaseMaintenance Maintenance { get; }

  /// <summary>
  /// Creates a maintainer covering all columns and coordinator storage.
  /// Its settings apply to all managed trees. Dispose it before the database.
  /// Individual columns also expose maintainers for independent tuning.
  /// </summary>
  IMaintainer CreateMaintainer();

  /// <summary>
  /// Creates a local live backup coordinator. Each generation contains the schema,
  /// every column and database transaction decisions at one consistent snapshot.
  /// Dispose the backup coordinator before the database.
  /// </summary>
  ILiveBackup CreateLiveBackup(
      LocalLiveBackupOptions storage,
      DistributedLiveBackupOptions options = null);

  /// <summary>
  /// Creates a live backup coordinator using custom object storage. It provides
  /// the same database-wide consistency as local backup and reuses immutable files.
  /// A generation is published only after all of its components are complete.
  /// </summary>
  ILiveBackup CreateLiveBackup(
      IBackupObjectStorage storage,
      ObjectStorageLiveBackupOptions storageOptions = null,
      DistributedLiveBackupOptions options = null);
}
