using ZoneTree.Backup;

namespace ZoneTree.Transactional.Distributed;

/// <summary>Creates, opens and restores in-process distributed databases.</summary>
public interface IDistributedDatabaseFactory
{
  /// <summary>Creates a database; fails if the database already exists.</summary>
  IDistributedDatabase CreateInProcess(DistributedDatabaseOptions options);

  /// <summary>Opens an existing database; fails if it does not exist.</summary>
  IDistributedDatabase OpenInProcess(DistributedDatabaseOptions options);

  /// <summary>
  /// Creates a new database or opens an existing one with the exact declared schema.
  /// </summary>
  IDistributedDatabase OpenOrCreateInProcess(DistributedDatabaseOptions options);

  /// <summary>
  /// Restores and opens a database from local backup. Null selects the latest generation.
  /// </summary>
  ValueTask<IDistributedDatabase> RestoreInProcessAsync(
      DistributedDatabaseOptions options,
      LocalLiveBackupOptions backup,
      long? generationId = null,
      CancellationToken cancellationToken = default);

  /// <summary>Restores a complete database generation from custom object storage.</summary>
  /// <inheritdoc cref="RestoreInProcessAsync(DistributedDatabaseOptions, LocalLiveBackupOptions, long?, CancellationToken)"/>
  ValueTask<IDistributedDatabase> RestoreInProcessAsync(
      DistributedDatabaseOptions options,
      IBackupObjectStorage storage,
      ObjectStorageLiveBackupOptions backupOptions = null,
      long? generationId = null,
      CancellationToken cancellationToken = default);
}
