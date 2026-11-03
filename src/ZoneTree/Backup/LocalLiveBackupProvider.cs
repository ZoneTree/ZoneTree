using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZoneTree.AbstractFileStream;
using ZoneTree.Options;
using ZoneTree.Segments.Disk;

namespace ZoneTree.Backup;

/// <summary>
/// Streams live backup generations into a local directory using asynchronous
/// file I/O.
/// </summary>
public sealed class LocalLiveBackupProvider
    : ILiveBackupStore,
      ILiveBackupSource
{
  const string ManifestFileName = "manifest.json";

  const string DataDirectory = "data";

  const string RecordsDirectory = "records";

  const string GenerationsDirectory = "generations";

  readonly Lock SyncRoot = new();

  readonly string BackupDirectory;

  readonly int BufferSize;

  readonly Action<FileStream> SyncFile;

  readonly Action<string> SyncDirectory;

  bool RootDirectoriesSynced;

  readonly LocalDirectoryManifest Manifest = new();

  // Reuse the original digest of immutable files, rather than blessing their
  // current bytes with a new digest when a later generation references them.
  readonly Dictionary<string, LocalLiveBackupFile> KnownFiles = new(StringComparer.Ordinal);

  bool ManifestLoaded;

  LocalLiveBackupGenerationCatalog ActiveGeneration;

  long LastGenerationId;

  public LocalLiveBackupProvider(
      string backupDirectory,
      int bufferSize = 128 * 1024)
      : this(new LocalLiveBackupOptions
      {
        Directory = backupDirectory,
        CopyBufferSize = bufferSize
      })
  {
  }

  public LocalLiveBackupProvider(
      LocalLiveBackupOptions options)
      : this(options, stream => stream.Flush(flushToDisk: true), DirectorySync.Flush)
  {
  }

  internal LocalLiveBackupProvider(LocalLiveBackupOptions options,
      Action<FileStream> syncFile, Action<string> syncDirectory)
  {
    ArgumentNullException.ThrowIfNull(options);
    options.Normalize();
    if (string.IsNullOrWhiteSpace(options.Directory))
      throw new ArgumentException(
          "Live backup directory is required.",
          nameof(options));
    BackupDirectory = options.Directory;
    BufferSize = options.CopyBufferSize;
    KeepLastGenerations = options.KeepLastGenerations;
    SyncFile = syncFile;
    SyncDirectory = syncDirectory;
  }

  public int? KeepLastGenerations { get; }

  public LocalLiveBackupGenerationCatalog ReadCurrentGeneration()
  {
    lock (SyncRoot)
      return ReadGenerationCatalog(ReadPublishedGenerationId());
  }

  public LocalLiveBackupGenerationCatalog ReadGeneration(long generationId)
  {
    lock (SyncRoot)
      return ReadGenerationCatalog(generationId);
  }

  public async Task<LiveBackupGeneration> ReadLatestGenerationAsync(
      CancellationToken cancellationToken)
  {
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var id = ReadPublishedGenerationId();
      try { return await ReadGenerationAsync(id, cancellationToken).ConfigureAwait(false); }
      // A new publication can retire the old head before we acquire its lease.
      catch (IOException) when (ReadPublishedGenerationId() != id) { }
    }
  }

  public Task<LiveBackupGeneration> ReadGenerationAsync(
      long generationId,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult(ReadLeasedGeneration(generationId));
  }

  LiveBackupGeneration ReadLeasedGeneration(long generationId)
  {
    var path = GetFullPath(GetGenerationPath(generationId));
    var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    try
    {
      // On Unix an open descriptor can outlive unlink. IDs are never reused,
      // so checking the name after acquiring its shared lock closes that race.
      if (!File.Exists(path)) throw new FileNotFoundException("Backup generation was retired.", path);
      using var reader = new StreamReader(lease, System.Text.Encoding.UTF8, true, 4096, leaveOpen: true);
      var catalog = DeserializeGenerationCatalog(reader.ReadToEnd());
      if (catalog == null || catalog.GenerationId != generationId)
        throw new LiveBackupIntegrityException(path, "Catalog generation ID does not match its filename.");
      var generation = ToLiveBackupGeneration(catalog);
      generation.Lease = lease;
      return generation;
    }
    catch { lease.Dispose(); throw; }
  }

  long ReadPublishedGenerationId()
  {
    using var stream = new FileStream(GetFullPath(ManifestFileName), FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    var manifest = JsonSerializer.Deserialize(stream, BackupJsonSerializerContext.Default.LocalDirectoryManifest);
    if (manifest == null || manifest.CurrentGenerationId <= 0)
      throw new InvalidOperationException("Local live backup does not contain a completed generation.");
    return manifest.CurrentGenerationId;
  }

  public Task<Stream> OpenSegmentFileAsync(
      LiveBackupFile file,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(file);
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult<Stream>(new FileStream(
        GetFullPath(GetSegmentBackupPath(file.FileName)),
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        BufferSize,
        FileOptions.Asynchronous));
  }

  public Task<Stream> OpenRecordBatchAsync(
      LiveBackupRecordBatch batch,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(batch);
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult<Stream>(new FileStream(
        GetFullPath(GetRecordBatchBackupPath(batch.BatchId)),
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        BufferSize,
        FileOptions.Asynchronous));
  }

  public Task<long> GetNextGenerationIdAsync(CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    EnsureRootDirectories();
    lock (SyncRoot)
    {
      LoadManifestIfNeeded();
      return Task.FromResult(++LastGenerationId);
    }
  }

  public Task BeginGenerationAsync(
      long generationId,
      DateTime startedAtUtc,
      LiveBackupTreeKind treeKind,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    EnsureRootDirectories();
    lock (SyncRoot)
    {
      ActiveGeneration = new LocalLiveBackupGenerationCatalog
      {
        Version = treeKind == LiveBackupTreeKind.Transactional ? 3 : 2,
        TreeKind = treeKind,
        GenerationId = generationId,
        StartedAtUtc = startedAtUtc.ToString("O")
      };
    }
    return Task.CompletedTask;
  }

  public Task<bool> UseSegmentAsync(
      long generationId,
      DiskSegmentFile file,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(file);

    var mustUpload = false;
    lock (SyncRoot)
    {
      var catalog = GetActiveGeneration(generationId);
      AddSegmentId(catalog, file.SegmentId);
      var backupFile = CreateCatalogFile(file);
      var path = GetFullPath(backupFile.BackupPath);
      if (File.Exists(path) && KnownFiles.TryGetValue(backupFile.BackupPath, out var known) && known.Sha256 != null)
      {
        RequireChecksum(known.Sha256, file.FileName);
        if (new FileInfo(path).Length != known.ByteLength)
          throw new LiveBackupIntegrityException(file.FileName, "Existing backup file length changed.");
        backupFile.ByteLength = known.ByteLength;
        backupFile.Sha256 = known.Sha256;
      }
      else
      {
        mustUpload = true;
      }
      AddFile(catalog, backupFile);
    }
    return Task.FromResult(mustUpload);
  }

  public async Task UploadSegmentFileAsync(
      long generationId,
      DiskSegmentFile file,
      Stream source,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(source);

    var backupFile = CreateCatalogFile(
        file,
        source.CanSeek ? source.Length : 0);
    var path = GetFullPath(backupFile.BackupPath);

    await WriteFileAtomicallyAsync(
        path,
        async destination =>
        {
          using var checkedDestination = new LiveBackupHashStream(destination, leaveOpen: true);
          await source.CopyToAsync(checkedDestination, BufferSize, cancellationToken).ConfigureAwait(false);
          backupFile.ByteLength = checkedDestination.BytesTransferred;
          backupFile.Sha256 = checkedDestination.GetChecksum();
        },
        cancellationToken, syncDirectory: false).ConfigureAwait(false);

    lock (SyncRoot)
    {
      AddFile(GetActiveGeneration(generationId), backupFile);
      KnownFiles[backupFile.BackupPath] = backupFile;
    }
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
      Justification = "Ownership transfers to LocalRecordBatchWriter through the hashing stream; construction failures dispose the stream.")]
  public async Task<ILiveBackupRecordWriter> OpenRecordWriterAsync(
      long generationId,
      LiveBackupRecordBatch batch,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(batch);

    cancellationToken.ThrowIfCancellationRequested();
    var recordBatch = new LocalLiveBackupRecordBatch
    {
      BatchId = batch.BatchId,
      BackupPath = GetRecordBatchBackupPath(batch.BatchId),
      CompressionMethod = batch.CompressionMethod,
      CompressionLevel = batch.CompressionLevel,
      CompressionBlockSize = batch.CompressionBlockSize,
      StartedAtUtc = DateTime.UtcNow.ToString("O")
    };

    var path = GetFullPath(recordBatch.BackupPath);
    var tempPath = GetTemporaryPath(path);
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(directory))
      Directory.CreateDirectory(directory);

    FileStream destination;
    try
    {
      destination = new FileStream(
          tempPath,
          FileMode.Create,
          FileAccess.Write,
          FileShare.Read,
          BufferSize,
          FileOptions.Asynchronous);
    }
    catch
    {
      DeleteFileIfExists(tempPath);
      throw;
    }

    try
    {
      LocalLiveBackupGenerationCatalog activeGeneration;
      lock (SyncRoot)
      {
        activeGeneration = GetActiveGeneration(generationId);
      }
      activeGeneration.RecordBatch = recordBatch;
      return new LocalRecordBatchWriter(
          destination,
          batch,
          recordBatch,
          tempPath,
          path,
          SyncFile);
    }
    catch
    {
      await destination.DisposeAsync();
      DeleteFileIfExists(tempPath);
      throw;
    }
  }

  public async Task CompleteGenerationAsync(
      long generationId,
      long lastOpIndex,
      CancellationToken cancellationToken)
  {
    LocalLiveBackupGenerationCatalog catalog;
    lock (SyncRoot)
    {
      catalog = GetActiveGeneration(generationId);
      catalog.LastOpIndex = lastOpIndex;
      ActiveGeneration = null;
    }

    // All uploads are closed and their contents synced. Persist their names
    // before any catalog can reference them, batching directory syncs per generation.
    SyncDirectory(GetFullPath(DataDirectory));
    SyncDirectory(GetFullPath(RecordsDirectory));
    await WriteGenerationAsync(catalog, cancellationToken);

    lock (SyncRoot)
    {
      Manifest.CurrentGenerationId = catalog.GenerationId;
      Manifest.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
    }
    await WriteManifestAsync(cancellationToken);
    lock (SyncRoot)
    {
      KnownFiles.Clear();
      foreach (var file in catalog.Files) KnownFiles[file.BackupPath] = file;
    }
    ApplyRetention(cancellationToken);
  }

  void EnsureRootDirectories()
  {
    lock (SyncRoot)
    {
      if (RootDirectoriesSynced) return;
      Directory.CreateDirectory(BackupDirectory);
      Directory.CreateDirectory(GetFullPath(DataDirectory));
      Directory.CreateDirectory(GetFullPath(RecordsDirectory));
      Directory.CreateDirectory(GetFullPath(GenerationsDirectory));
      DirectorySync.FlushTree(Path.GetFullPath(BackupDirectory), SyncDirectory);
      RootDirectoriesSynced = true;
    }
  }

  void LoadManifestIfNeeded()
  {
    if (ManifestLoaded)
      return;

    var path = GetFullPath(ManifestFileName);
    if (File.Exists(path))
    {
      var loaded = JsonSerializer.Deserialize(
          File.ReadAllText(path),
          BackupJsonSerializerContext.Default.LocalDirectoryManifest);
      if (loaded != null)
      {
        Manifest.Version = loaded.Version;
        Manifest.CreatedAtUtc = loaded.CreatedAtUtc;
        Manifest.UpdatedAtUtc = loaded.UpdatedAtUtc;
        Manifest.CurrentGenerationId = loaded.CurrentGenerationId;
      }
    }
    LastGenerationId = Manifest.CurrentGenerationId;
    // A complete catalog may precede a failed/crashed manifest publication.
    // Never overwrite it, or its generation-specific record batch, on restart.
    foreach (var catalogPath in Directory.GetFiles(GetFullPath(GenerationsDirectory), "*.json"))
      if (long.TryParse(Path.GetFileNameWithoutExtension(catalogPath), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        LastGenerationId = Math.Max(LastGenerationId, id);
    if (Manifest.CurrentGenerationId > 0)
      foreach (var file in ReadGenerationCatalog(Manifest.CurrentGenerationId).Files)
        KnownFiles[file.BackupPath] = file;
    ManifestLoaded = true;
  }

  async Task WriteGenerationAsync(
      LocalLiveBackupGenerationCatalog catalog,
      CancellationToken cancellationToken)
  {
    SortCatalog(catalog);
    var path = GetFullPath(GetGenerationPath(catalog.GenerationId));
    var bytes = JsonSerializer.SerializeToUtf8Bytes(
        ToJsonModel(catalog),
        BackupJsonSerializerContext.Default.LocalLiveBackupGenerationCatalogJsonModel);
    await WriteFileAtomicallyAsync(
        path,
        destination => destination.WriteAsync(bytes, cancellationToken).AsTask(),
        cancellationToken);
  }

  async Task WriteManifestAsync(CancellationToken cancellationToken)
  {
    var path = GetFullPath(ManifestFileName);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(
        Manifest,
        BackupJsonSerializerContext.Default.LocalDirectoryManifest);
    await WriteFileAtomicallyAsync(
        path,
        destination => destination.WriteAsync(bytes, cancellationToken).AsTask(),
        cancellationToken);
  }

  async Task WriteFileAtomicallyAsync(
      string path,
      Func<Stream, Task> writeAsync,
      CancellationToken cancellationToken,
      bool syncDirectory = true)
  {
    var tempPath = GetTemporaryPath(path);
    try
    {
      var directory = Path.GetDirectoryName(path);
      if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);

      await using (var destination = new FileStream(
          tempPath,
          FileMode.Create,
          FileAccess.Write,
          FileShare.None,
          BufferSize,
          FileOptions.Asynchronous))
      {
        await writeAsync(destination);
        cancellationToken.ThrowIfCancellationRequested();
        SyncFile(destination);
      }
      CommitTemporaryFile(tempPath, path);
      if (syncDirectory) SyncDirectory(Path.GetDirectoryName(path));
    }
    catch
    {
      DeleteFileIfExists(tempPath);
      throw;
    }
  }

  static string GetTemporaryPath(string path)
  {
    return path + "." + Guid.NewGuid().ToString("N") + ".tmp";
  }

  static void CommitTemporaryFile(
      string tempPath,
      string path)
  {
    if (File.Exists(path))
    {
      File.Replace(tempPath, path, null);
      return;
    }
    try
    {
      File.Move(tempPath, path);
    }
    catch (IOException) when (File.Exists(path))
    {
      File.Replace(tempPath, path, null);
    }
  }

  static void DeleteFileIfExists(string path)
  {
    if (File.Exists(path))
      File.Delete(path);
  }

  void ApplyRetention(CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!KeepLastGenerations.HasValue)
      return;

    foreach (var name in new[] { DataDirectory, RecordsDirectory, GenerationsDirectory })
      if ((File.GetAttributes(GetFullPath(name)) & FileAttributes.ReparsePoint) != 0)
        throw new LiveBackupIntegrityException(name, "Backup subdirectories must not be symbolic links.");

    var generations = ReadGenerationCatalogs()
        .OrderByDescending(x => x.GenerationId)
        .ToList();
    var published = ReadPublishedGenerationId();
    if (!generations.Any(x => x.GenerationId == published))
      throw new LiveBackupIntegrityException(ManifestFileName, "Published generation catalog is missing.");
    var retained = generations
        .Where(x => x.GenerationId <= published)
        .Take(KeepLastGenerations.Value)
        .ToList();

    // Retire catalogs before their data. Open restore sources hold shared
    // catalog leases. DeleteOnClose removes the name while we still own the
    // exclusive lock, so a new restore cannot acquire a retired generation.
    foreach (var generation in generations.Except(retained).ToArray())
    {
      cancellationToken.ThrowIfCancellationRequested();
      var path = GetFullPath(GetGenerationPath(generation.GenerationId));
      try
      {
        using var retired = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.None, 1, FileOptions.DeleteOnClose);
      }
      catch (IOException) when (File.Exists(path)) { }
      if (File.Exists(path)) retained.Add(generation); // Active reader or an unsuccessful delete: keep its files.
    }

    // A retired catalog must not reappear after its files have been reclaimed.
    SyncDirectory(GetFullPath(GenerationsDirectory));

    var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    var retainedPaths = retained.SelectMany(GetBackupPaths).Select(GetFullPath).ToHashSet(comparer);
    // This also reclaims files left by an interrupted upload or catalog removal.
    // These two directories are owned by the backup store, never the live tree.
    foreach (var name in new[] { DataDirectory, RecordsDirectory })
    {
      var directory = GetFullPath(name);
      foreach (var path in Directory.GetFiles(directory))
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (!retainedPaths.Contains(Path.GetFullPath(path))) File.Delete(path);
      }
    }
    foreach (var path in Directory.GetFiles(GetFullPath(GenerationsDirectory), "*.tmp")
        .Concat(Directory.GetFiles(Path.GetFullPath(BackupDirectory), "manifest.json.*.tmp")))
    {
      cancellationToken.ThrowIfCancellationRequested();
      File.Delete(path);
    }
    SyncDirectory(GetFullPath(DataDirectory));
    SyncDirectory(GetFullPath(RecordsDirectory));
    SyncDirectory(GetFullPath(GenerationsDirectory));
    SyncDirectory(Path.GetFullPath(BackupDirectory));
    lock (SyncRoot)
      foreach (var path in KnownFiles.Keys.Where(x => !retainedPaths.Contains(GetFullPath(x))).ToArray())
        KnownFiles.Remove(path);
  }

  static string GetSegmentBackupPath(string fileName)
  {
    if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(fileName))
      throw new LiveBackupIntegrityException(fileName, "Expected a segment filename without directories.");
    return DataDirectory + "/" + fileName;
  }

  static string GetRecordBatchBackupPath(long batchId)
  {
    return RecordsDirectory + "/" +
        batchId.ToString("D20", CultureInfo.InvariantCulture) + ".bin";
  }

  static string GetGenerationPath(long generationId)
  {
    return GenerationsDirectory + "/" +
        generationId.ToString("D20", CultureInfo.InvariantCulture) + ".json";
  }

  string GetFullPath(string relativePath)
  {
    var path = relativePath
        .Replace('/', Path.DirectorySeparatorChar)
        .Replace('\\', Path.DirectorySeparatorChar);
    var directory = Path.GetFullPath(BackupDirectory);
    var root = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
    var fullPath = Path.GetFullPath(Path.Combine(BackupDirectory, path));
    if (!fullPath.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
      throw new LiveBackupIntegrityException(relativePath, "Path escapes the backup directory.");
    return fullPath;
  }

  List<LocalLiveBackupGenerationCatalog> ReadGenerationCatalogs()
  {
    var directory = GetFullPath(GenerationsDirectory);
    if (!Directory.Exists(directory))
      return new List<LocalLiveBackupGenerationCatalog>();

    var result = new List<LocalLiveBackupGenerationCatalog>();
    foreach (var path in Directory.GetFiles(directory, "*.json"))
    {
      var catalog = DeserializeGenerationCatalog(File.ReadAllText(path));
      if (catalog == null || !long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None,
          CultureInfo.InvariantCulture, out var id) || catalog.GenerationId != id)
        throw new LiveBackupIntegrityException(path, "Catalog generation ID does not match its filename.");
      result.Add(catalog);
    }
    return result;
  }

  LocalLiveBackupGenerationCatalog ReadGenerationCatalog(long generationId)
  {
    var path = GetFullPath(GetGenerationPath(generationId));
    if (!File.Exists(path))
      throw new FileNotFoundException(
          "Local live backup generation catalog was not found.",
          path);
    var catalog = DeserializeGenerationCatalog(File.ReadAllText(path));
    if (catalog == null || catalog.GenerationId != generationId)
      throw new LiveBackupIntegrityException(path, "Catalog generation ID does not match its filename.");
    return catalog;
  }

  static LocalLiveBackupGenerationCatalog DeserializeGenerationCatalog(
      string json)
  {
    var model = JsonSerializer.Deserialize(
        json,
        BackupJsonSerializerContext.Default.LocalLiveBackupGenerationCatalogJsonModel);
    if (model == null)
      return null;
    if (model.Version is not (1 or 2 or 3))
      throw new NotSupportedException($"Unsupported live backup catalog version {model.Version}.");
    if (model.TreeKind != (model.Version == 3 ? LiveBackupTreeKind.Transactional : LiveBackupTreeKind.Plain))
      throw new LiveBackupIntegrityException($"generation {model.GenerationId}", "Invalid tree kind for catalog version.");
    var catalog = new LocalLiveBackupGenerationCatalog
    {
      Version = model.Version,
      TreeKind = model.TreeKind,
      GenerationId = model.GenerationId,
      LastOpIndex = model.LastOpIndex,
      StartedAtUtc = model.StartedAtUtc,
      Files = model.Files,
      RecordBatch = model.RecordBatch
    };
    if (model.SegmentIds != null)
      catalog.SegmentIds.AddRange(model.SegmentIds);
    return catalog;
  }

  static LocalLiveBackupGenerationCatalogJsonModel ToJsonModel(
      LocalLiveBackupGenerationCatalog catalog)
  {
    return new LocalLiveBackupGenerationCatalogJsonModel
    {
      Version = catalog.Version,
      TreeKind = catalog.TreeKind,
      GenerationId = catalog.GenerationId,
      LastOpIndex = catalog.LastOpIndex,
      StartedAtUtc = catalog.StartedAtUtc,
      SegmentIds = catalog.SegmentIds,
      Files = catalog.Files,
      RecordBatch = catalog.RecordBatch
    };
  }

  LocalLiveBackupGenerationCatalog GetActiveGeneration(long generationId)
  {
    if (ActiveGeneration != null &&
        ActiveGeneration.GenerationId == generationId)
    {
      return ActiveGeneration;
    }

    throw new InvalidOperationException(
        "Live backup generation has not been started.");
  }

  static LocalLiveBackupFile CreateCatalogFile(
      DiskSegmentFile file,
      long byteLength = 0)
  {
    return new LocalLiveBackupFile
    {
      SegmentId = file.SegmentId,
      Order = file.Order,
      FileName = file.FileName,
      BackupPath = GetSegmentBackupPath(file.FileName),
      RecordCount = file.RecordCount,
      ByteLength = byteLength,
      CopiedAtUtc = DateTime.UtcNow.ToString("O")
    };
  }

  static void AddSegmentId(
      LocalLiveBackupGenerationCatalog catalog,
      long segmentId)
  {
    if (segmentId <= 0)
      return;
    if (!catalog.SegmentIds.Contains(segmentId))
      catalog.SegmentIds.Add(segmentId);
  }

  static void AddFile(
      LocalLiveBackupGenerationCatalog catalog,
      LocalLiveBackupFile file)
  {
    if (string.IsNullOrWhiteSpace(file.BackupPath))
      return;

    catalog.Files.RemoveAll(x =>
        string.Equals(x.BackupPath, file.BackupPath, StringComparison.Ordinal));
    catalog.Files.Add(file);
  }

  static IEnumerable<string> GetBackupPaths(
      LocalLiveBackupGenerationCatalog catalog)
  {
    foreach (var file in catalog.Files)
      yield return GetSegmentBackupPath(file.FileName);

    if (catalog.RecordBatch != null)
      yield return GetRecordBatchBackupPath(catalog.RecordBatch.BatchId);
  }

  static void SortCatalog(LocalLiveBackupGenerationCatalog catalog)
  {
    catalog.Files = catalog.Files
        .OrderBy(x => x.BackupPath, StringComparer.Ordinal)
        .ToList();
  }

  static LiveBackupGeneration ToLiveBackupGeneration(
      LocalLiveBackupGenerationCatalog catalog)
  {
    if (catalog.Version >= 2)
    {
      foreach (var file in catalog.Files) RequireChecksum(file.Sha256, file.FileName);
      if (catalog.RecordBatch != null) RequireChecksum(catalog.RecordBatch.Sha256, $"record batch {catalog.RecordBatch.BatchId}");
    }
    if (catalog.RecordBatch != null && !catalog.RecordBatch.Completed)
      throw new LiveBackupIntegrityException($"generation {catalog.GenerationId}", "Record batch is incomplete.");
    return new LiveBackupGeneration
    {
      TreeKind = catalog.TreeKind,
      GenerationId = catalog.GenerationId,
      LastOpIndex = catalog.LastOpIndex,
      SegmentIds = [.. catalog.SegmentIds],
      Files = [.. catalog.Files
          .Select(x => new LiveBackupFile
          {
            SegmentId = x.SegmentId,
            Order = x.Order,
            FileName = x.FileName,
            RecordCount = x.RecordCount,
            ByteLength = x.ByteLength,
            Sha256 = x.Sha256
          })],
      RecordBatch = ToRecordBatch(catalog.RecordBatch)
    };
  }

  static void RequireChecksum(string checksum, string file)
  {
    if (checksum == null || checksum.Length != 64 || !checksum.All(Uri.IsHexDigit))
      throw new LiveBackupIntegrityException(file, "Missing or invalid SHA-256 checksum in backup catalog.");
  }

  static LiveBackupRecordBatch ToRecordBatch(
      LocalLiveBackupRecordBatch batch)
  {
    if (batch == null || !batch.Completed)
      return null;
    return new LiveBackupRecordBatch
    {
      BatchId = batch.BatchId,
      RecordCount = batch.RecordCount,
      CompressionMethod = batch.CompressionMethod,
      CompressionLevel = batch.CompressionLevel,
      CompressionBlockSize = batch.CompressionBlockSize,
      UncompressedLength = batch.UncompressedLength,
      StoredLength = batch.StoredLength,
      Sha256 = batch.Sha256
    };
  }

  sealed class LocalRecordBatchWriter : ILiveBackupRecordWriter
  {
    readonly LiveBackupRecordBatchWriter Writer;

    readonly LiveBackupRecordBatch SourceBatch;

    readonly LocalLiveBackupRecordBatch CatalogBatch;

    readonly string TempPath;

    readonly string DestinationPath;

    readonly FileStream Destination;

    readonly Action<FileStream> SyncFile;

    long RecordCount;

    public LocalRecordBatchWriter(
        FileStream destination,
        LiveBackupRecordBatch sourceBatch,
        LocalLiveBackupRecordBatch catalogBatch,
        string tempPath,
        string destinationPath,
        Action<FileStream> syncFile)
    {
      Destination = destination;
      SyncFile = syncFile;
      Writer = new LiveBackupRecordBatchWriter(destination, sourceBatch, leaveOpen: true);
      SourceBatch = sourceBatch;
      CatalogBatch = catalogBatch;
      TempPath = tempPath;
      DestinationPath = destinationPath;
    }

    public Task WriteAsync(
        LiveBackupRecord record,
        CancellationToken cancellationToken)
    {
      ++RecordCount;
      return Writer.WriteAsync(record, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
      try
      {
        await Writer.DisposeAsync();
        SyncFile(Destination);
        await Destination.DisposeAsync();
        CommitTemporaryFile(TempPath, DestinationPath);
        CatalogBatch.RecordCount = RecordCount;
        CatalogBatch.UncompressedLength = SourceBatch.UncompressedLength;
        CatalogBatch.StoredLength = SourceBatch.StoredLength;
        CatalogBatch.Sha256 = SourceBatch.Sha256;
        CatalogBatch.CompletedAtUtc = DateTime.UtcNow.ToString("O");
        CatalogBatch.Completed = true;
      }
      catch
      {
        await Destination.DisposeAsync();
        DeleteFileIfExists(TempPath);
        throw;
      }
    }
  }
}

internal sealed class LocalDirectoryManifest
{
  public int Version { get; set; } = 1;

  public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");

  public string UpdatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");

  public long CurrentGenerationId { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LocalDirectoryManifest))]
[JsonSerializable(typeof(LocalLiveBackupGenerationCatalogJsonModel))]
internal sealed partial class BackupJsonSerializerContext : JsonSerializerContext
{
}

internal sealed class LocalLiveBackupGenerationCatalogJsonModel
{
  public int Version { get; set; } = 1;

  public LiveBackupTreeKind TreeKind { get; set; }

  public long GenerationId { get; set; }

  public long LastOpIndex { get; set; }

  public string StartedAtUtc { get; set; }

  public List<long> SegmentIds { get; set; } = [];

  public List<LocalLiveBackupFile> Files { get; set; } = [];

  public LocalLiveBackupRecordBatch RecordBatch { get; set; }
}

public sealed class LocalLiveBackupGenerationCatalog
{
  /// <summary>Version 2 requires SHA-256 digests; version 3 identifies transactional storage. Missing versions denote legacy version 1.</summary>
  public int Version { get; set; } = 1;

  public LiveBackupTreeKind TreeKind { get; set; }

  public long GenerationId { get; set; }

  public long LastOpIndex { get; set; }

  public string StartedAtUtc { get; set; }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Performance matters more. List is stable for years.")]
  public List<long> SegmentIds { get; } = [];

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Performance matters more. List is stable for years.")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Probably, but we need to make it writable now.")]
  public List<LocalLiveBackupFile> Files { get; set; } = [];

  public LocalLiveBackupRecordBatch RecordBatch { get; set; }
}

public sealed class LocalLiveBackupRecordBatch
{
  public long BatchId { get; set; }

  public string BackupPath { get; set; }

  public long RecordCount { get; set; }

  public CompressionMethod CompressionMethod { get; set; }

  public int CompressionLevel { get; set; }

  public int CompressionBlockSize { get; set; }

  public long UncompressedLength { get; set; }

  public long StoredLength { get; set; }

  public string Sha256 { get; set; }

  public string StartedAtUtc { get; set; }

  public string CompletedAtUtc { get; set; }

  public bool Completed { get; set; }
}

public sealed class LocalLiveBackupFile
{
  public long SegmentId { get; set; }

  public int Order { get; set; }

  public string FileName { get; set; }

  public string BackupPath { get; set; }

  public long RecordCount { get; set; }

  public long ByteLength { get; set; }

  public string Sha256 { get; set; }

  public string CopiedAtUtc { get; set; }
}
