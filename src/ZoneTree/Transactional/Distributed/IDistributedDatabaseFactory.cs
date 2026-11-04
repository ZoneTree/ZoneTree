using ZoneTree.Backup;

namespace ZoneTree.Transactional.Distributed;

/// <summary>Proposed creation and recovery entry points; no runtime implementation yet.</summary>
/// <remarks>
/// All columns are local to this process in the first implementation. The database
/// owns their trees and the coordinator; attaching independently opened trees is
/// unsupported. Recovery resolves one outcome per database transaction before any
/// column becomes available. Placement and transport are separate from transaction
/// semantics; future process/machine entry points must use the same commit protocol.
/// </remarks>
public interface IDistributedDatabaseFactory
{
  /// <summary>Creates a database; fails if the database already exists.</summary>
  IDistributedDatabase CreateInProcess(DistributedDatabaseOptions options);

  /// <summary>Opens and recovers an existing database; fails if it does not exist.</summary>
  IDistributedDatabase OpenInProcess(DistributedDatabaseOptions options);

  /// <summary>
  /// Creates a new database or opens an existing one with the exact declared schema.
  /// An existing column directory is not independently opened or created if the
  /// database manifest or recovery state is missing or inconsistent.
  /// </summary>
  IDistributedDatabase OpenOrCreateInProcess(DistributedDatabaseOptions options);

  /// <summary>
  /// Restores a complete database generation from local storage into an empty
  /// destination, validates the schema and opens it. Null selects the latest complete
  /// generation. Independently captured column backups are not database generations.
  /// A cancelled or failed restore never opens a partially restored database.
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
