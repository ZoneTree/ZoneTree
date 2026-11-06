using ZoneTree.AbstractFileStream;
using ZoneTree.Backup;
using ZoneTree.Logger;

namespace ZoneTree.Cluster;

/// <summary>
/// Restores a cluster backup generation into an empty data directory, then opens the cluster.
/// </summary>
/// <remarks>
/// The catalog comes from the generation's manifest. Each domain records its cut as durable
/// and every later commit as rolled back, so every coordinated column, whenever it opens,
/// keeps exactly the commits at or below the cut. Each column's storage comes from its own
/// store, restored with the types of its definition, so every backed-up column must be
/// declared in the options.
/// </remarks>
internal static class ClusterRestore
{
  internal static async Task<IZoneTreeClusterHost> RunAsync(ZoneTreeClusterFactory factory,
      ZoneTreeClusterOptions options, ClusterBackupLocation location, long? generationId,
      CancellationToken cancellationToken)
  {
    var declared = ZoneTreeClusterFactory.Validate(options);
    var directory = Path.GetFullPath(options.DataDirectory);
    var files = options.FileStreamProvider ?? new LocalFileStreamProvider();
    var catalogPath = Path.Combine(directory, "catalog");
    var columnsPath = Path.Combine(directory, "columns");
    if (files.DirectoryExists(catalogPath) || files.DirectoryExists(columnsPath))
      throw new LiveBackupRestoreTargetAlreadyExistsException();

    var manifest = await ReadManifestAsync(location, generationId, cancellationToken).ConfigureAwait(false);
    var columns = MatchDefinitions(manifest, declared);
    var settings = ColumnStorageSettings.From(options);
    var directoryExisted = Directory.Exists(directory);
    Directory.CreateDirectory(directory);
    var leasePath = Path.Combine(directory, "database.lock");
    try
    {
      using (new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
      {
        ClusterCatalog.Restore(directory, options.Logger, files, manifest.Cluster,
            RestoredDomains(manifest), manifest.Columns);
        foreach (var (record, definition, columnGeneration) in columns)
        {
          cancellationToken.ThrowIfCancellationRequested();
          await definition.RestoreStorageAsync(settings, options.Encodings, record,
              ZoneTreeCluster.ColumnDirectory(directory, record.Id),
              location.CreateColumnStore(record.Id).Source, columnGeneration).ConfigureAwait(false);
        }
      }
    }
    catch
    {
      // A failed restore leaves nothing behind, so it can be tried again.
      try
      {
        foreach (var path in new[] { catalogPath, columnsPath })
        {
          if (files.DirectoryExists(path))
            files.DeleteDirectory(path, recursive: true);
        }
        File.Delete(leasePath);
        if (!directoryExisted && !Directory.EnumerateFileSystemEntries(directory).Any())
          Directory.Delete(directory);
      }
      catch (Exception error)
      {
        options.Logger.LogError(error);
      }
      throw;
    }
    return factory.OpenInProcess(options);
  }

  /// <summary>
  /// The domains as restored: each cut is durable, and every later commit up to the domain's
  /// bound is rolled back. The domains are clean, so the open does not recover them again.
  /// </summary>
  static IEnumerable<DomainRecord> RestoredDomains(ClusterBackupManifest manifest)
  {
    foreach (var domain in manifest.Domains)
    {
      var cut = manifest.Cuts.FirstOrDefault(cut => cut.DomainId == domain.Id);
      if (cut.DomainId != domain.Id)
      {
        yield return domain with { IsClean = true };
        continue;
      }
      var bound = Math.Max(domain.TimestampBound, cut.TimestampBound);
      yield return domain with
      {
        TimestampBound = bound,
        DurableSequence = cut.Sequence,
        RolledBackRanges = cut.Sequence < bound
            ? [.. domain.RolledBackRanges, new SequenceRange(cut.Sequence, bound)]
            : domain.RolledBackRanges,
        IsClean = true
      };
    }
  }

  /// <summary>The backed-up columns with their definitions and their stores' generations.</summary>
  static List<(ColumnRecord Record, ColumnDefinition Definition, long Generation)> MatchDefinitions(
      ClusterBackupManifest manifest, List<(ColumnDefinition Column, string Domain)> declared)
  {
    var generations = manifest.ColumnGenerations.ToDictionary(column => column.ColumnId, column => column.GenerationId);
    var matched = new List<(ColumnRecord, ColumnDefinition, long)>();
    var missing = new List<string>();
    foreach (var record in manifest.Columns)
    {
      // A column whose storage was never created has nothing to restore; it is created on open.
      if (!generations.TryGetValue(record.Id, out var generation))
        continue;
      var domain = record.DomainId == 0 ? null : manifest.Domains.First(domain => domain.Id == record.DomainId).Name;
      var definition = declared.FirstOrDefault(item =>
          item.Column.Name == record.Name && item.Domain == domain && item.Column.Kind == record.Kind).Column;
      if (definition == null)
        missing.Add(record.Name);
      else
        matched.Add((record, definition, generation));
    }
    if (missing.Count > 0)
      throw new ArgumentException(
          $"A restore needs the definition of every backed-up column, with its domain and kind. Declare: {string.Join(", ", missing)}.");
    return matched;
  }

  /// <summary>The requested generation, or the newest one whose column generations all remain.</summary>
  static async Task<ClusterBackupManifest> ReadManifestAsync(ClusterBackupLocation location, long? generationId,
      CancellationToken cancellationToken)
  {
    if (generationId is { } id)
    {
      var bytes = await location.ReadManifestAsync(id, cancellationToken).ConfigureAwait(false)
          ?? throw new LiveBackupGenerationException(id, "restoring the cluster",
              new FileNotFoundException("The cluster backup generation does not exist."));
      return ClusterBackupManifest.Decode(bytes);
    }
    var ids = await location.ListManifestsAsync(cancellationToken).ConfigureAwait(false);
    foreach (var candidate in ids.OrderByDescending(id => id))
    {
      var bytes = await location.ReadManifestAsync(candidate, cancellationToken).ConfigureAwait(false);
      if (bytes == null)
        continue;
      var manifest = ClusterBackupManifest.Decode(bytes);
      if (await AreColumnsReadableAsync(location, manifest, cancellationToken).ConfigureAwait(false))
        return manifest;
    }
    throw new LiveBackupException("The backup has no restorable cluster generation.");
  }

  static async Task<bool> AreColumnsReadableAsync(ClusterBackupLocation location, ClusterBackupManifest manifest,
      CancellationToken cancellationToken)
  {
    foreach (var column in manifest.ColumnGenerations)
    {
      try
      {
        await using var generation = await location.CreateColumnStore(column.ColumnId).Source
            .ReadGenerationAsync(column.GenerationId, cancellationToken).ConfigureAwait(false);
      }
      catch (Exception) when (!cancellationToken.IsCancellationRequested)
      {
        return false;
      }
    }
    return true;
  }
}
