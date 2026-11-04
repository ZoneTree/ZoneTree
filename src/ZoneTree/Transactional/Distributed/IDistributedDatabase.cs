using ZoneTree.Backup;
using ZoneTree.Logger;

namespace ZoneTree.Transactional.Distributed;

/// <summary>
/// A database with independently typed transactional and raw columns.
/// </summary>
/// <remarks>
/// Transactions share one snapshot and commit atomically across accessed transactional columns.
/// Raw columns are accessed independently.
/// The database and its column handles are thread-safe.
/// </remarks>
public interface IDistributedDatabase : IDisposable, IAsyncDisposable
{
  /// <summary>
  /// Gets a typed handle to an existing column. Names are case-sensitive.
  /// </summary>
  /// <exception cref="KeyNotFoundException">The column does not exist.</exception>
  /// <exception cref="InvalidOperationException">The column has different types.</exception>
  IDistributedColumn<TKey, TValue> GetColumn<TKey, TValue>(string name);

  /// <summary>Gets a typed handle to a non-transactional column. Names are case-sensitive.</summary>
  IDistributedRawColumn<TKey, TValue> GetRawColumn<TKey, TValue>(string name);

  /// <summary>
  /// Begins a read/write transaction with the requested isolation level across transactional columns.
  /// </summary>
  /// <exception cref="Exceptions.ZoneTreeIsReadOnlyException">The database is read-only.</exception>
  IDistributedTransaction BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable);

  /// <summary>
  /// Asynchronously begins a read/write transaction.
  /// </summary>
  ValueTask<IDistributedTransaction> BeginTransactionAsync(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Begins a consistent read-only snapshot across transactional columns.
  /// </summary>
  IDistributedReadTransaction BeginReadOnlyTransaction();

  /// <summary>Asynchronously acquires the same consistent, read-only database snapshot.</summary>
  ValueTask<IDistributedReadTransaction> BeginReadOnlyTransactionAsync(
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Makes completed raw writes and transactions committed before this call durable across power loss or an OS crash.
  /// </summary>
  Task SyncWALAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// The background WAL sync interval. Zero disables automatic syncing.
  /// </summary>
  TimeSpan AutoSyncInterval { get; set; }

  /// <summary>
  /// Prevents new writing transactions and raw column writes when set.
  /// </summary>
  bool IsReadOnly { get; set; }

  /// <summary>The database logger.</summary>
  ILogger Logger { get; }

  /// <summary>Database-wide maintenance and diagnostics.</summary>
  IDistributedDatabaseMaintenance Maintenance { get; }

  /// <summary>
  /// Creates a maintainer for the database.
  /// </summary>
  IMaintainer CreateMaintainer();

  /// <summary>
  /// Creates a consistent database-wide live backup in local storage.
  /// </summary>
  ILiveBackup CreateLiveBackup(
      LocalLiveBackupOptions storage,
      DistributedLiveBackupOptions options = null);

  /// <summary>
  /// Creates a consistent database-wide live backup in custom object storage.
  /// </summary>
  ILiveBackup CreateLiveBackup(
      IBackupObjectStorage storage,
      ObjectStorageLiveBackupOptions storageOptions = null,
      DistributedLiveBackupOptions options = null);
}
