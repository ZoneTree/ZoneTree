namespace ZoneTree.Backup;

/// <summary>The storage operations required by ObjectStorageLiveBackupProvider.</summary>
/// <remarks>
/// Per-object operations must be linearizable across clients. Committed objects must be
/// complete, durable, and immediately readable/listable. Listing must enumerate every
/// object present before enumeration starts that remains present throughout it; concurrent
/// additions or deletions may also be returned. Versions are opaque tokens identifying
/// the exact bytes returned by a read (for example, an ETag). Keys are case-sensitive,
/// opaque strings. Delete is idempotent. Implementations must support concurrent
/// transfers. These requirements include persistence of local filesystem
/// names if a filesystem is used; flushing bytes to the OS alone is insufficient.
/// </remarks>
public interface IBackupObjectStorage
{
  /// <summary>Returns a read of one committed version, or null when absent.</summary>
  ValueTask<BackupObjectRead> TryOpenReadAsync(string key, CancellationToken cancellationToken);

  /// <summary>Returns committed object metadata, or null when absent.</summary>
  ValueTask<BackupObjectInfo> GetInfoAsync(string key, CancellationToken cancellationToken);

  /// <summary>
  /// Opens a streaming, create-only upload. Commit must fail if the key exists.
  /// Uncommitted uploads must not be readable or listed; disposal aborts them.
  /// Disposal must never delete a published object, including after a lost commit acknowledgment.
  /// </summary>
  ValueTask<BackupObjectWrite> CreateAsync(string key, CancellationToken cancellationToken);

  /// <summary>
  /// Atomically writes bytes only if the current version equals expectedVersion.
  /// A null expectedVersion means the key must be absent. Returns null on conflict.
  /// Success returns the new committed version. An exception can have an uncertain
  /// outcome; callers reconcile through another read rather than overwriting blindly.
  /// </summary>
  ValueTask<BackupObjectInfo> CompareExchangeAsync(string key, ReadOnlyMemory<byte> content,
      string expectedVersion, CancellationToken cancellationToken);

  IAsyncEnumerable<BackupObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken);

  ValueTask DeleteAsync(string key, CancellationToken cancellationToken);
}

public sealed record BackupObjectInfo(string Key, long Length, string Version);

/// <summary>A consistent read and its associated resources. The caller owns this handle.</summary>
public abstract class BackupObjectRead : IAsyncDisposable
{
  public abstract BackupObjectInfo Info { get; }
  public abstract Stream Stream { get; }
  public abstract ValueTask DisposeAsync();
}

/// <summary>A create-only upload. Disposing an uncommitted upload aborts it.</summary>
public abstract class BackupObjectWrite : IAsyncDisposable
{
  public abstract Stream Stream { get; }
  public abstract ValueTask<BackupObjectInfo> CommitAsync(CancellationToken cancellationToken);
  public abstract ValueTask DisposeAsync();
}
