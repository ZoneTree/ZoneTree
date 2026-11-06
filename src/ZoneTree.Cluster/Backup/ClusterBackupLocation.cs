using System.Globalization;
using ZoneTree.AbstractFileStream;
using ZoneTree.Backup;

namespace ZoneTree.Cluster;

/// <summary>
/// Where a cluster's live backups live: a store of its own for each column, and the cluster
/// generation manifests beside them.
/// </summary>
/// <remarks>
/// Each cluster generation publishes one generation in every column's store, so the columns
/// keep one generation more than the cluster: a crash while the columns publish leaves one
/// that no manifest names, and the previous cluster generation still finds its own.
/// </remarks>
internal abstract class ClusterBackupLocation
{
  /// <summary>The number of cluster generations to keep, or null to keep them all.</summary>
  internal abstract int? KeepLastGenerations { get; }

  /// <summary>The store of a column's backups, which is also their source for restore.</summary>
  internal abstract LiveBackupProvider CreateColumnStore(long columnId);

  internal abstract Task<IReadOnlyList<long>> ListManifestsAsync(CancellationToken cancellationToken);

  /// <summary>Reads a manifest, or returns null if it does not exist.</summary>
  internal abstract Task<byte[]> ReadManifestAsync(long generationId, CancellationToken cancellationToken);

  /// <summary>Writes a manifest durably. Each generation id is written once.</summary>
  internal abstract Task WriteManifestAsync(long generationId, byte[] content, CancellationToken cancellationToken);

  internal abstract Task DeleteManifestAsync(long generationId, CancellationToken cancellationToken);

  /// <summary>A column's store and source.</summary>
  internal readonly record struct LiveBackupProvider(ILiveBackupStore Store, ILiveBackupSource Source);

  protected int? ColumnKeepLastGenerations => KeepLastGenerations + 1;

  // Fixed-width names keep one column's names from being a prefix of another's.
  protected static string ColumnName(long columnId) => columnId.ToString("D20", CultureInfo.InvariantCulture);

  protected static string ManifestName(long generationId)
      => generationId.ToString("D20", CultureInfo.InvariantCulture) + ".manifest";

  protected static bool TryParseManifestName(string name, out long generationId)
  {
    generationId = 0;
    return name.EndsWith(".manifest", StringComparison.Ordinal) &&
        long.TryParse(name.AsSpan(0, name.Length - ".manifest".Length), NumberStyles.None,
            CultureInfo.InvariantCulture, out generationId) && generationId > 0;
  }

  internal static ClusterBackupLocation Local(LocalLiveBackupOptions options) => new LocalLocation(options);

  internal static ClusterBackupLocation ObjectStorage(IBackupObjectStorage storage,
      ObjectStorageLiveBackupOptions options) => new ObjectStorageLocation(storage, options ?? new());

  sealed class LocalLocation : ClusterBackupLocation
  {
    readonly LocalLiveBackupOptions Options;
    readonly string ManifestDirectory;

    internal LocalLocation(LocalLiveBackupOptions options)
    {
      ArgumentNullException.ThrowIfNull(options);
      options.Normalize();
      if (string.IsNullOrWhiteSpace(options.Directory))
        throw new ArgumentException("A live backup directory is required.", nameof(options));
      Options = options;
      ManifestDirectory = Path.Combine(options.Directory, "cluster");
    }

    internal override int? KeepLastGenerations => Options.KeepLastGenerations;

    internal override LiveBackupProvider CreateColumnStore(long columnId)
    {
      var provider = new LocalLiveBackupProvider(new LocalLiveBackupOptions
      {
        Directory = Path.Combine(Options.Directory, "columns", ColumnName(columnId)),
        CopyBufferSize = Options.CopyBufferSize,
        KeepLastGenerations = ColumnKeepLastGenerations
      });
      return new(provider, provider);
    }

    internal override Task<IReadOnlyList<long>> ListManifestsAsync(CancellationToken cancellationToken)
    {
      var ids = new List<long>();
      if (Directory.Exists(ManifestDirectory))
      {
        foreach (var path in Directory.GetFiles(ManifestDirectory))
          if (TryParseManifestName(Path.GetFileName(path), out var id))
            ids.Add(id);
      }
      return Task.FromResult<IReadOnlyList<long>>(ids);
    }

    internal override async Task<byte[]> ReadManifestAsync(long generationId, CancellationToken cancellationToken)
    {
      var path = Path.Combine(ManifestDirectory, ManifestName(generationId));
      return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    internal override async Task WriteManifestAsync(long generationId, byte[] content, CancellationToken cancellationToken)
    {
      Directory.CreateDirectory(ManifestDirectory);
      var path = Path.Combine(ManifestDirectory, ManifestName(generationId));
      var temporary = path + ".tmp";
      var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
      await using (stream.ConfigureAwait(false))
      {
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        // Only a synchronous flush reaches the disk.
#pragma warning disable CA1849
        stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
      }
      File.Move(temporary, path);
      DirectorySync.Flush(ManifestDirectory);
    }

    internal override Task DeleteManifestAsync(long generationId, CancellationToken cancellationToken)
    {
      var path = Path.Combine(ManifestDirectory, ManifestName(generationId));
      if (File.Exists(path))
        File.Delete(path);
      return Task.CompletedTask;
    }
  }

  sealed class ObjectStorageLocation : ClusterBackupLocation
  {
    readonly IBackupObjectStorage Storage;
    readonly ObjectStorageLiveBackupOptions Options;
    readonly string ManifestPrefix;

    internal ObjectStorageLocation(IBackupObjectStorage storage, ObjectStorageLiveBackupOptions options)
    {
      ArgumentNullException.ThrowIfNull(storage);
      if (string.IsNullOrWhiteSpace(options.Prefix))
        throw new ArgumentException("An object storage prefix is required.", nameof(options));
      Storage = storage;
      Options = options;
      ManifestPrefix = options.Prefix + "/cluster/";
    }

    internal override int? KeepLastGenerations => Options.KeepLastGenerations;

    internal override LiveBackupProvider CreateColumnStore(long columnId)
    {
      var provider = new ObjectStorageLiveBackupProvider(Storage, new ObjectStorageLiveBackupOptions
      {
        Prefix = Options.Prefix + "/columns/" + ColumnName(columnId),
        KeepLastGenerations = ColumnKeepLastGenerations,
        MaximumMetadataBytes = Options.MaximumMetadataBytes
      });
      return new(provider, provider);
    }

    internal override async Task<IReadOnlyList<long>> ListManifestsAsync(CancellationToken cancellationToken)
    {
      var ids = new List<long>();
      await foreach (var info in Storage.ListAsync(ManifestPrefix, cancellationToken).ConfigureAwait(false))
      {
        if (info.Key.StartsWith(ManifestPrefix, StringComparison.Ordinal) &&
            TryParseManifestName(info.Key[ManifestPrefix.Length..], out var id))
          ids.Add(id);
      }
      return ids;
    }

    internal override async Task<byte[]> ReadManifestAsync(long generationId, CancellationToken cancellationToken)
    {
      var read = await Storage.TryOpenReadAsync(ManifestPrefix + ManifestName(generationId), cancellationToken)
          .ConfigureAwait(false);
      if (read == null)
        return null;
      await using (read.ConfigureAwait(false))
      {
        using var buffer = new MemoryStream();
        await read.Stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
      }
    }

    internal override async Task WriteManifestAsync(long generationId, byte[] content, CancellationToken cancellationToken)
    {
      var write = await Storage.CreateAsync(ManifestPrefix + ManifestName(generationId), cancellationToken)
          .ConfigureAwait(false);
      await using (write.ConfigureAwait(false))
      {
        await write.Stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
      }
    }

    internal override Task DeleteManifestAsync(long generationId, CancellationToken cancellationToken)
        => Storage.DeleteAsync(ManifestPrefix + ManifestName(generationId), cancellationToken).AsTask();
  }
}
