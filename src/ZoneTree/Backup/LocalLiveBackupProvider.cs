using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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

  readonly LocalDirectoryManifest Manifest = new();

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
  }

  public int? KeepLastGenerations { get; }

  public LocalLiveBackupGenerationCatalog ReadCurrentGeneration()
  {
    lock (SyncRoot)
    {
      LoadManifestIfNeeded();
      if (Manifest.CurrentGenerationId <= 0)
        throw new InvalidOperationException(
            "Local live backup does not contain a completed generation.");
      return ReadGenerationCatalog(Manifest.CurrentGenerationId);
    }
  }

  public LocalLiveBackupGenerationCatalog ReadGeneration(long generationId)
  {
    lock (SyncRoot)
      return ReadGenerationCatalog(generationId);
  }

  public Task<LiveBackupGeneration> ReadLatestGenerationAsync(
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    lock (SyncRoot)
    {
      LoadManifestIfNeeded();
      if (Manifest.CurrentGenerationId <= 0)
        throw new InvalidOperationException(
            "Local live backup does not contain a completed generation.");
      return Task.FromResult(ToLiveBackupGeneration(
          ReadGenerationCatalog(Manifest.CurrentGenerationId)));
    }
  }

  public Task<LiveBackupGeneration> ReadGenerationAsync(
      long generationId,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    lock (SyncRoot)
      return Task.FromResult(ToLiveBackupGeneration(
          ReadGenerationCatalog(generationId)));
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
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    EnsureRootDirectories();
    lock (SyncRoot)
    {
      ActiveGeneration = new LocalLiveBackupGenerationCatalog
      {
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
      if (File.Exists(path))
      {
        backupFile.ByteLength = new FileInfo(path).Length;
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
          await source.CopyToAsync(destination, BufferSize, cancellationToken);
        },
        cancellationToken);

    lock (SyncRoot)
    {
      AddFile(GetActiveGeneration(generationId), backupFile);
    }
  }

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
          path);
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

    await WriteGenerationAsync(catalog, cancellationToken);

    lock (SyncRoot)
    {
      Manifest.CurrentGenerationId = catalog.GenerationId;
      Manifest.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
    }
    await WriteManifestAsync(cancellationToken);
    ApplyRetention(cancellationToken);
  }

  void EnsureRootDirectories()
  {
    Directory.CreateDirectory(BackupDirectory);
    Directory.CreateDirectory(GetFullPath(DataDirectory));
    Directory.CreateDirectory(GetFullPath(RecordsDirectory));
    Directory.CreateDirectory(GetFullPath(GenerationsDirectory));
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
      CancellationToken cancellationToken)
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
        await destination.FlushAsync(cancellationToken);
      }
      CommitTemporaryFile(tempPath, path);
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
    // Retention is applied at the backup-path level, not at the generation level
    // alone. We first read all generation catalogs and select the newest
    // KeepLastGenerations catalogs as the retained set. Every file referenced by
    // those retained catalogs is protected from deletion, including segment files,
    // record batches, the manifest, and the retained generation catalog files.
    //
    // Older generations are then scanned for files to remove. A file from an older
    // generation is deleted only if its backup path is not referenced by any
    // retained generation. This is important for reused segment files: when
    // UseSegmentAsync reuses an existing segment file, it still records that file
    // in the new generation catalog. Therefore, even if the original older
    // generation is removed, the physical segment file is preserved as long as a
    // retained generation still references the same backup path.
    cancellationToken.ThrowIfCancellationRequested();
    if (!KeepLastGenerations.HasValue)
      return;

    var generations = ReadGenerationCatalogs()
        .OrderByDescending(x => x.GenerationId)
        .ToList();

    var retained = generations
        .Take(KeepLastGenerations.Value)
        .ToList();
    var retainedPaths = retained
        .SelectMany(GetBackupPaths)
        .Append(ManifestFileName)
        .Concat(retained.Select(x => GetGenerationPath(x.GenerationId)))
        .ToHashSet(StringComparer.Ordinal);

    var deleted = generations
        .Skip(KeepLastGenerations.Value)
        .SelectMany(x => GetBackupPaths(x)
            .Append(GetGenerationPath(x.GenerationId)))
        .Where(x => !retainedPaths.Contains(x))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    foreach (var backupPath in deleted)
      DeleteFile(backupPath, cancellationToken);
  }

  void DeleteFile(
      string backupPath,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var path = GetFullPath(backupPath);
    if (File.Exists(path))
      File.Delete(path);
  }

  static string GetSegmentBackupPath(string fileName)
  {
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
    return Path.Combine(BackupDirectory, path);
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
      if (catalog != null)
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
    return DeserializeGenerationCatalog(File.ReadAllText(path));
  }

  static LocalLiveBackupGenerationCatalog DeserializeGenerationCatalog(
      string json)
  {
    var model = JsonSerializer.Deserialize(
        json,
        BackupJsonSerializerContext.Default.LocalLiveBackupGenerationCatalogJsonModel);
    if (model == null)
      return null;
    var catalog = new LocalLiveBackupGenerationCatalog
    {
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
      if (!string.IsNullOrWhiteSpace(file.BackupPath))
        yield return file.BackupPath;

    if (!string.IsNullOrWhiteSpace(catalog.RecordBatch?.BackupPath))
      yield return catalog.RecordBatch.BackupPath;
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
    return new LiveBackupGeneration
    {
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
            ByteLength = x.ByteLength
          })],
      RecordBatch = ToRecordBatch(catalog.RecordBatch)
    };
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
      StoredLength = batch.StoredLength
    };
  }

  sealed class LocalRecordBatchWriter : ILiveBackupRecordWriter
  {
    readonly LiveBackupRecordBatchWriter Writer;

    readonly LiveBackupRecordBatch SourceBatch;

    readonly LocalLiveBackupRecordBatch CatalogBatch;

    readonly string TempPath;

    readonly string DestinationPath;

    long RecordCount;

    public LocalRecordBatchWriter(
        Stream destination,
        LiveBackupRecordBatch sourceBatch,
        LocalLiveBackupRecordBatch catalogBatch,
        string tempPath,
        string destinationPath)
    {
      Writer = new LiveBackupRecordBatchWriter(destination, sourceBatch);
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
        CommitTemporaryFile(TempPath, DestinationPath);
        CatalogBatch.RecordCount = RecordCount;
        CatalogBatch.UncompressedLength = SourceBatch.UncompressedLength;
        CatalogBatch.StoredLength = SourceBatch.StoredLength;
        CatalogBatch.CompletedAtUtc = DateTime.UtcNow.ToString("O");
        CatalogBatch.Completed = true;
      }
      catch
      {
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
  public long GenerationId { get; set; }

  public long LastOpIndex { get; set; }

  public string StartedAtUtc { get; set; }

  public List<long> SegmentIds { get; set; } = [];

  public List<LocalLiveBackupFile> Files { get; set; } = [];

  public LocalLiveBackupRecordBatch RecordBatch { get; set; }
}

public sealed class LocalLiveBackupGenerationCatalog
{
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

  public string CopiedAtUtc { get; set; }
}
