using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZoneTree.Segments.Disk;

namespace ZoneTree.Backup;

/// <summary>
/// Streams complete backup generations into immutable objects. A conditionally
/// updated manifest coordinates publication, restore pins and reclamation across clients.
/// </summary>
/// <remarks>
/// Use one backup writer per prefix. Starting another attempt fences any unfinished
/// attempt, including one left by a crashed writer. A fenced writer cannot publish.
/// Concurrent restore clients are protected by persistent pins, without expiring leases.
/// The storage adapter owns object durability and must satisfy IBackupObjectStorage.
/// </remarks>
public sealed class ObjectStorageLiveBackupProvider : ILiveBackupStore, ILiveBackupSource
{
  const ulong ManifestMagic = 0x314B41424F545A; // ZTOBAK1
  const int ManifestHeaderSize = 40;
  readonly IBackupObjectStorage Storage;
  readonly string Prefix;
  readonly string ManifestKey;
  readonly string GenerationsPrefix;
  readonly int? KeepLastGenerations;
  readonly int MaximumMetadataBytes;
  readonly Lock Gate = new();
  long AllocatedId;
  ObjectBackupCatalog Active;
  Dictionary<string, LiveBackupFile> KnownFiles = new(StringComparer.Ordinal);

  public ObjectStorageLiveBackupProvider(IBackupObjectStorage storage, ObjectStorageLiveBackupOptions options = null)
  {
    Storage = storage ?? throw new ArgumentNullException(nameof(storage));
    options ??= new ObjectStorageLiveBackupOptions();
    var prefix = options.Prefix?.TrimEnd('/');
    if (string.IsNullOrWhiteSpace(prefix) || prefix[0] == '/' || prefix.Contains('\\', StringComparison.Ordinal) ||
        prefix.Split('/').Any(x => x is "" or "." or ".."))
      throw new ArgumentException("Prefix must be a nonempty relative object namespace.", nameof(options));
    if (options.KeepLastGenerations is <= 0 || options.MaximumMetadataBytes < 1024)
      throw new global::System.ArgumentOutOfRangeException(nameof(options));
    Prefix = prefix + "/";
    ManifestKey = Prefix + "manifest.ztb";
    GenerationsPrefix = Prefix + "generations/";
    KeepLastGenerations = options.KeepLastGenerations;
    MaximumMetadataBytes = options.MaximumMetadataBytes;
  }

  public async Task<long> GetNextGenerationIdAsync(CancellationToken cancellationToken)
  {
    while (true)
    {
      var (state, version) = await ReadStateAsync(create: true, cancellationToken).ConfigureAwait(false);
      var id = checked(state.LastAllocatedId + 1);
      state.LastAllocatedId = state.ActiveGenerationId = id;
      if (!await WriteStateAsync(state, version, cancellationToken).ConfigureAwait(false)) continue;

      // Only reuse files from the head observed by this allocation. GC may protect
      // that head or a newer one, but must never resurrect an arbitrary older file.
      var known = new Dictionary<string, LiveBackupFile>(StringComparer.Ordinal);
      if (state.CurrentGenerationId != 0)
      {
        var catalog = await ReadCatalogAsync(state.Generations.Single(x => x.Id == state.CurrentGenerationId), cancellationToken).ConfigureAwait(false);
        foreach (var file in catalog.Files) known.Add(FileIdentity(file.SegmentId, file.FileName), file);
      }
      lock (Gate) { AllocatedId = id; Active = null; KnownFiles = known; }
      return id;
    }
  }

  public Task BeginGenerationAsync(long generationId, DateTime startedAtUtc, LiveBackupTreeKind treeKind, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (treeKind is not (LiveBackupTreeKind.Plain or LiveBackupTreeKind.Transactional))
      throw new global::System.ArgumentOutOfRangeException(nameof(treeKind));
    lock (Gate)
    {
      if (generationId != AllocatedId || generationId <= 0 || Active != null)
        throw new InvalidOperationException("Allocate a new generation before beginning it.");
      Active = new ObjectBackupCatalog
      {
        GenerationId = generationId, TreeKind = treeKind,
        StartedAtUnixSeconds = new DateTimeOffset(DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()
      };
    }
    return Task.CompletedTask;
  }

  public async Task<bool> UseSegmentAsync(long generationId, DiskSegmentFile file, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(file);
    ValidateFileName(file.FileName);
    if (file.SegmentId <= 0 || file.Order < 0 || file.RecordCount < 0) throw new global::System.ArgumentOutOfRangeException(nameof(file));
    LiveBackupFile known;
    lock (Gate)
    {
      GetActive(generationId);
      KnownFiles.TryGetValue(FileIdentity(file.SegmentId, file.FileName), out known);
    }
    if (known == null) return true;
    var info = await Storage.GetInfoAsync(known.StorageKey, cancellationToken).ConfigureAwait(false);
    if (info == null) return true;
    ValidateInfo(info, known.StorageKey, known.ByteLength);
    lock (Gate) AddFile(GetActive(generationId), new LiveBackupFile
    {
      SegmentId = file.SegmentId, FileName = file.FileName, Order = file.Order,
      RecordCount = file.RecordCount, ByteLength = known.ByteLength, Sha256 = known.Sha256, StorageKey = known.StorageKey
    });
    return false;
  }

  public async Task UploadSegmentFileAsync(long generationId, DiskSegmentFile file, Stream source, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(source);
    ValidateFileName(file.FileName);
    lock (Gate) GetActive(generationId);
    var key = SegmentKey(generationId, file.SegmentId, file.FileName);
    await using var upload = await Storage.CreateAsync(key, cancellationToken).ConfigureAwait(false);
    using var hash = new LiveBackupHashStream(upload.Stream, leaveOpen: true);
    await source.CopyToAsync(hash, cancellationToken).ConfigureAwait(false);
    var fileHash = hash.GetChecksum();
    var info = await upload.CommitAsync(cancellationToken).ConfigureAwait(false);
    ValidateInfo(info, key, hash.BytesTransferred);
    lock (Gate) AddFile(GetActive(generationId), new LiveBackupFile
    {
      SegmentId = file.SegmentId, Order = file.Order, FileName = file.FileName, RecordCount = file.RecordCount,
      StorageKey = key, ByteLength = hash.BytesTransferred, Sha256 = fileHash
    });
  }

  public async Task<ILiveBackupRecordWriter> OpenRecordWriterAsync(long generationId, LiveBackupRecordBatch batch, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(batch);
    if (batch.BatchId != generationId) throw new ArgumentException("Record batches must use their generation ID.", nameof(batch));
    lock (Gate)
    {
      var catalog = GetActive(generationId);
      if (catalog.RecordBatch != null || catalog.RecordBatchOpen) throw new InvalidOperationException("A generation has only one record batch.");
      catalog.RecordBatchOpen = true;
    }
    var key = GenerationPrefix(generationId) + "records.bin";
    var upload = await Storage.CreateAsync(key, cancellationToken).ConfigureAwait(false);
    try
    {
      batch.StorageKey = key;
      var writer = new ObjectRecordWriter(upload, batch, () =>
      {
        lock (Gate)
        {
          var catalog = GetActive(generationId);
          catalog.RecordBatch = batch;
          catalog.RecordBatchOpen = false;
        }
      });
      return writer;
    }
    catch { await upload.DisposeAsync().ConfigureAwait(false); throw; }
  }

  public async Task CompleteGenerationAsync(long generationId, long lastOpIndex, CancellationToken cancellationToken)
  {
    ObjectBackupCatalog catalog;
    lock (Gate)
    {
      catalog = GetActive(generationId);
      if (catalog.RecordBatchOpen) throw new InvalidOperationException("Complete the record writer before publishing its generation.");
      catalog.LastOpIndex = lastOpIndex;
      catalog.CompletedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      catalog.Files = catalog.Files.OrderBy(x => x.Order).ThenBy(x => x.FileName, StringComparer.Ordinal).ToList();
    }
    ValidateCatalog(catalog, generationId);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog, ObjectBackupJsonContext.Default.ObjectBackupCatalog);
    CheckMetadataSize(bytes.Length);
    var catalogKey = CatalogKey(generationId);
    await using (var upload = await Storage.CreateAsync(catalogKey, cancellationToken).ConfigureAwait(false))
    {
      await upload.Stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
      ValidateInfo(await upload.CommitAsync(cancellationToken).ConfigureAwait(false), catalogKey, bytes.Length);
    }
    var reference = new ObjectBackupGenerationReference { Id = generationId, Length = bytes.Length, Sha256 = Digest(bytes) };
    while (true)
    {
      var (state, version) = await ReadStateAsync(false, cancellationToken).ConfigureAwait(false);
      if (state.ActiveGenerationId != generationId)
        throw new InvalidOperationException("This backup attempt was superseded by another writer; it cannot publish.");
      state.CurrentGenerationId = generationId;
      state.ActiveGenerationId = 0;
      state.Generations.Add(reference);
      PruneReferences(state);
      if (await WriteStateAsync(state, version, cancellationToken).ConfigureAwait(false)) break;
    }
    lock (Gate) { if (Active == catalog) Active = null; }
    await ApplyRetentionAsync(cancellationToken).ConfigureAwait(false);
  }

  public Task<LiveBackupGeneration> ReadLatestGenerationAsync(CancellationToken cancellationToken)
      => ReadPinnedGenerationAsync(null, cancellationToken);

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1512:Use ArgumentOutOfRangeException throw helper", Justification = "Also targets .NET 6 and 7.")]
  public Task<LiveBackupGeneration> ReadGenerationAsync(long generationId, CancellationToken cancellationToken)
  {
    if (generationId <= 0) throw new global::System.ArgumentOutOfRangeException(nameof(generationId));
    return ReadPinnedGenerationAsync(generationId, cancellationToken);
  }

  async Task<LiveBackupGeneration> ReadPinnedGenerationAsync(long? requested, CancellationToken cancellationToken)
  {
    ObjectBackupGenerationReference reference;
    var pinId = Guid.NewGuid().ToString("N");
    while (true)
    {
      var (state, version) = await ReadStateAsync(false, cancellationToken).ConfigureAwait(false);
      var id = requested ?? state.CurrentGenerationId;
      reference = state.Generations.SingleOrDefault(x => x.Id == id)
          ?? throw new FileNotFoundException($"Backup generation {id} is not retained.");
      state.RestorePins.Add(new(pinId, id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
      if (await WriteStateAsync(state, version, cancellationToken).ConfigureAwait(false)) break;
    }
    var lease = new RestorePin(this, pinId);
    try
    {
      var catalog = await ReadCatalogAsync(reference, cancellationToken).ConfigureAwait(false);
      var generation = LiveBackupGeneration.CreateWithAsyncLease(lease);
      generation.GenerationId = catalog.GenerationId;
      generation.TreeKind = catalog.TreeKind;
      generation.LastOpIndex = catalog.LastOpIndex;
      generation.Files = catalog.Files.ToArray();
      generation.SegmentIds = catalog.Files.Select(x => x.SegmentId).Distinct().ToArray();
      generation.RecordBatch = catalog.RecordBatch;
      return generation;
    }
    catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
  }

  public Task<Stream> OpenSegmentFileAsync(LiveBackupFile file, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(file);
    ValidateOwnedKey(file.StorageKey);
    return OpenObjectStreamAsync(file.StorageKey, file.ByteLength, cancellationToken);
  }

  public Task<Stream> OpenRecordBatchAsync(LiveBackupRecordBatch batch, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(batch);
    ValidateOwnedKey(batch.StorageKey);
    return OpenObjectStreamAsync(batch.StorageKey, batch.StoredLength, cancellationToken);
  }

  async Task<Stream> OpenObjectStreamAsync(string key, long length, CancellationToken cancellationToken)
  {
    var read = await Storage.TryOpenReadAsync(key, cancellationToken).ConfigureAwait(false)
        ?? throw new FileNotFoundException("Backup object is missing.", key);
    try { ValidateInfo(read.Info, key, length); return new ObjectReadStream(read); }
    catch { await read.DisposeAsync().ConfigureAwait(false); throw; }
  }

  /// <summary>Lists persistent pins, including those left by crashed restore consumers.</summary>
  public async Task<IReadOnlyList<ObjectStorageBackupRestorePin>> ListRestorePinsAsync(CancellationToken cancellationToken = default)
      => (await ReadStateAsync(false, cancellationToken).ConfigureAwait(false)).State.RestorePins.ToArray();

  /// <summary>
  /// Lists retained generation IDs, newest first, at the time of the read.
  /// Reading a generation acquires its pin; listing alone does not protect it from retirement.
  /// </summary>
  public async Task<IReadOnlyList<long>> ListGenerationIdsAsync(CancellationToken cancellationToken = default)
      => (await ReadStateAsync(false, cancellationToken).ConfigureAwait(false)).State.Generations
          .Select(x => x.Id).OrderByDescending(x => x).ToArray();

  /// <summary>
  /// Releases a pin. For an abandoned pin, first ensure its restore consumer has stopped.
  /// The next retention pass can delete that generation and its unshared objects.
  /// </summary>
  public async Task ReleaseRestorePinAsync(string pinId, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(pinId);
    while (true)
    {
      var (state, version) = await ReadStateAsync(false, cancellationToken).ConfigureAwait(false);
      if (state.RestorePins.RemoveAll(x => x.Id == pinId) == 0) return;
      if (await WriteStateAsync(state, version, cancellationToken).ConfigureAwait(false)) return;
    }
  }

  /// <summary>Applies retention and removes abandoned generation objects. Safe alongside writers and pinned restores.</summary>
  public async Task ApplyRetentionAsync(CancellationToken cancellationToken = default)
  {
    while (true)
    {
      ObjectBackupState snapshot;
      while (true)
      {
        var (state, version) = await ReadStateAsync(false, cancellationToken).ConfigureAwait(false);
        if (!PruneReferences(state)) { snapshot = state; break; }
        if (await WriteStateAsync(state, version, cancellationToken).ConfigureAwait(false)) { snapshot = state; break; }
      }
      var protectedKeys = new HashSet<string>(StringComparer.Ordinal);
      // Build the entire protected set before deleting anything. A concurrently
      // retired catalog may disappear here; retry with the new retained set.
      try
      {
        foreach (var reference in snapshot.Generations)
        {
          var catalog = await ReadCatalogAsync(reference, cancellationToken).ConfigureAwait(false);
          protectedKeys.Add(CatalogKey(reference.Id));
          foreach (var file in catalog.Files) protectedKeys.Add(file.StorageKey);
          if (catalog.RecordBatch != null) protectedKeys.Add(catalog.RecordBatch.StorageKey);
        }
      }
      catch (Exception error) when (error is FileNotFoundException or LiveBackupIntegrityException)
      {
        var (current, _) = await ReadStateAsync(false, cancellationToken).ConfigureAwait(false);
        if (snapshot.Generations.Any(x => !current.Generations.Any(y => y.Id == x.Id))) continue;
        throw;
      }
      await foreach (var info in Storage.ListAsync(GenerationsPrefix, cancellationToken).ConfigureAwait(false))
      {
        var id = ParseGenerationKey(info.Key);
        // A newer allocation is outside this sweep. The allocation already active
        // at capture is excluded too, so its in-flight uploads cannot be reclaimed.
        if (id <= snapshot.LastAllocatedId && id != snapshot.ActiveGenerationId && !protectedKeys.Contains(info.Key))
          await Storage.DeleteAsync(info.Key, cancellationToken).ConfigureAwait(false);
      }
      return;
    }
  }

  bool PruneReferences(ObjectBackupState state)
  {
    if (!KeepLastGenerations.HasValue) return false;
    var pinned = state.RestorePins.Select(x => x.GenerationId).ToHashSet();
    var keep = state.Generations.OrderByDescending(x => x.Id).Take(KeepLastGenerations.Value).Select(x => x.Id).ToHashSet();
    return state.Generations.RemoveAll(x => !keep.Contains(x.Id) && !pinned.Contains(x.Id)) != 0;
  }

  async Task<(ObjectBackupState State, string Version)> ReadStateAsync(bool create, CancellationToken cancellationToken)
  {
    while (true)
    {
      await using var read = await Storage.TryOpenReadAsync(ManifestKey, cancellationToken).ConfigureAwait(false);
      if (read != null)
      {
        ValidateInfo(read.Info, ManifestKey, read.Info.Length);
        if (string.IsNullOrEmpty(read.Info.Version)) throw Integrity(ManifestKey, "Storage returned no object version.");
        var bytes = await ReadMetadataAsync(read, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < ManifestHeaderSize || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != ManifestMagic ||
            !SHA256.HashData(bytes.AsSpan(ManifestHeaderSize)).AsSpan().SequenceEqual(bytes.AsSpan(8, 32)))
          throw Integrity(ManifestKey, "Manifest framing or checksum is invalid.");
        var state = JsonSerializer.Deserialize(bytes.AsSpan(ManifestHeaderSize), ObjectBackupJsonContext.Default.ObjectBackupState);
        ValidateState(state);
        return (state, read.Info.Version);
      }
      if (!create) throw new FileNotFoundException("Object storage contains no backup manifest.", ManifestKey);
      var hasObjects = false;
      await foreach (var _ in Storage.ListAsync(GenerationsPrefix, cancellationToken).ConfigureAwait(false)) { hasObjects = true; break; }
      if (hasObjects)
      {
        await using var raced = await Storage.TryOpenReadAsync(ManifestKey, cancellationToken).ConfigureAwait(false);
        if (raced != null) continue;
        throw Integrity(ManifestKey, "Manifest is missing while generation objects exist; refusing to reuse IDs.");
      }
      if (await WriteStateAsync(new ObjectBackupState(), null, cancellationToken).ConfigureAwait(false)) continue;
    }
  }

  async Task<bool> WriteStateAsync(ObjectBackupState state, string version, CancellationToken cancellationToken)
  {
    // Keep bytes unique even when a pin is added and removed again. This prevents
    // ABA with content-derived ETags, without requiring backend object versioning.
    state.Revision = checked(state.Revision + 1);
    var payload = JsonSerializer.SerializeToUtf8Bytes(state, ObjectBackupJsonContext.Default.ObjectBackupState);
    CheckMetadataSize(checked(payload.Length + ManifestHeaderSize));
    var bytes = new byte[payload.Length + ManifestHeaderSize];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, ManifestMagic);
    SHA256.HashData(payload).CopyTo(bytes.AsSpan(8));
    payload.CopyTo(bytes.AsSpan(ManifestHeaderSize));
    var info = await Storage.CompareExchangeAsync(ManifestKey, bytes, version, cancellationToken).ConfigureAwait(false);
    if (info == null)
    {
      await Task.Delay(1, cancellationToken).ConfigureAwait(false);
      return false;
    }
    ValidateInfo(info, ManifestKey, bytes.Length);
    if (string.IsNullOrEmpty(info.Version) || info.Version == version)
      throw Integrity(ManifestKey, "Storage returned no new committed object version.");
    return true;
  }

  async Task<ObjectBackupCatalog> ReadCatalogAsync(ObjectBackupGenerationReference reference, CancellationToken cancellationToken)
  {
    var key = CatalogKey(reference.Id);
    await using var read = await Storage.TryOpenReadAsync(key, cancellationToken).ConfigureAwait(false)
        ?? throw new FileNotFoundException("Backup catalog is missing.", key);
    ValidateInfo(read.Info, key, reference.Length);
    var bytes = await ReadMetadataAsync(read, cancellationToken).ConfigureAwait(false);
    if (!string.Equals(Digest(bytes), reference.Sha256, StringComparison.Ordinal)) throw Integrity(key, "Catalog checksum mismatch.");
    var catalog = JsonSerializer.Deserialize(bytes, ObjectBackupJsonContext.Default.ObjectBackupCatalog);
    ValidateCatalog(catalog, reference.Id);
    return catalog;
  }

  async Task<byte[]> ReadMetadataAsync(BackupObjectRead read, CancellationToken cancellationToken)
  {
    CheckMetadataSize(read.Info.Length);
    using var output = new MemoryStream((int)read.Info.Length);
    var buffer = new byte[16 * 1024];
    int count;
    while ((count = await read.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
    {
      CheckMetadataSize(output.Length + count);
      await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
    }
    if (output.Length != read.Info.Length) throw Integrity(read.Info.Key, "Metadata length mismatch.");
    return output.ToArray();
  }

  void ValidateState(ObjectBackupState state)
  {
    if (state == null || state.Version != 1 || state.Revision <= 0 || state.LastAllocatedId < 0 || state.CurrentGenerationId < 0 ||
        state.CurrentGenerationId > state.LastAllocatedId || state.ActiveGenerationId < 0 ||
        (state.ActiveGenerationId != 0 && (state.ActiveGenerationId != state.LastAllocatedId || state.ActiveGenerationId <= state.CurrentGenerationId)) ||
        state.Generations == null || state.RestorePins == null ||
        state.Generations.Any(x => x == null || x.Id <= 0 || x.Id > state.CurrentGenerationId || x.Length <= 0 || !ValidDigest(x.Sha256)) ||
        state.Generations.Select(x => x.Id).Distinct().Count() != state.Generations.Count ||
        (state.CurrentGenerationId != 0 && !state.Generations.Any(x => x.Id == state.CurrentGenerationId)) ||
        state.RestorePins.Any(x => x == null || string.IsNullOrEmpty(x.Id) || !state.Generations.Any(g => g.Id == x.GenerationId)) ||
        state.RestorePins.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != state.RestorePins.Count)
      throw Integrity(ManifestKey, "Invalid backup manifest state.");
  }

  void ValidateCatalog(ObjectBackupCatalog catalog, long id)
  {
    if (catalog == null || catalog.Version != 1 || catalog.GenerationId != id || catalog.LastOpIndex < 0 ||
        catalog.TreeKind is not (LiveBackupTreeKind.Plain or LiveBackupTreeKind.Transactional) || catalog.Files == null ||
        catalog.Files.Any(x => x == null) || catalog.Files.Select(x => FileIdentity(x.SegmentId, x.FileName)).Distinct(StringComparer.Ordinal).Count() != catalog.Files.Count)
      throw Integrity(CatalogKey(id), "Invalid backup catalog.");
    foreach (var file in catalog.Files)
    {
      ValidateFileName(file.FileName);
      var origin = ParseGenerationKey(file.StorageKey);
      if (file.SegmentId <= 0 || file.Order < 0 || file.RecordCount < 0 || file.ByteLength < 0 || !ValidDigest(file.Sha256) ||
          origin > id || file.StorageKey != SegmentKey(origin, file.SegmentId, file.FileName))
        throw Integrity(file.FileName, "Invalid immutable file reference.");
    }
    var batch = catalog.RecordBatch;
    if (batch != null && (batch.BatchId != id || batch.StorageKey != GenerationPrefix(id) + "records.bin" ||
        batch.RecordCount < 0 || batch.StoredLength < 0 || batch.UncompressedLength < 0 || !ValidDigest(batch.Sha256)))
      throw Integrity(CatalogKey(id), "Invalid record batch metadata.");
    if (catalog.TreeKind == LiveBackupTreeKind.Transactional && batch == null)
      throw Integrity(CatalogKey(id), "Transactional backups require a completed record batch.");
  }

  ObjectBackupCatalog GetActive(long id) => Active != null && Active.GenerationId == id
      ? Active : throw new InvalidOperationException("Backup generation is not active.");

  static void AddFile(ObjectBackupCatalog catalog, LiveBackupFile file)
  {
    catalog.Files.RemoveAll(x => x.SegmentId == file.SegmentId && x.FileName == file.FileName);
    catalog.Files.Add(file);
  }

  static string FileIdentity(long id, string name) => id.ToString(CultureInfo.InvariantCulture) + "/" + name;
  string GenerationPrefix(long id) => GenerationsPrefix + id.ToString("D20", CultureInfo.InvariantCulture) + "/";
  string CatalogKey(long id) => GenerationPrefix(id) + "catalog.json";
  string SegmentKey(long generationId, long segmentId, string name)
      => GenerationPrefix(generationId) + "segments/" + segmentId.ToString("D20", CultureInfo.InvariantCulture) + "/" + name;

  long ParseGenerationKey(string key)
  {
    ValidateOwnedKey(key);
    var suffix = key.AsSpan(GenerationsPrefix.Length);
    if (suffix.Length <= 21 || suffix[20] != '/' || !long.TryParse(suffix[..20], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
      throw Integrity(key, "Invalid generation object key.");
    return id;
  }

  void ValidateOwnedKey(string key)
  {
    if (string.IsNullOrEmpty(key) || !key.StartsWith(GenerationsPrefix, StringComparison.Ordinal) ||
        key.Contains('\\', StringComparison.Ordinal) || key.Split('/').Any(x => x is "" or "." or ".."))
      throw Integrity(key, "Object key is outside the backup namespace.");
  }

  static void ValidateFileName(string name)
  {
    if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0)
      throw Integrity(name, "Expected a segment basename.");
  }

  void CheckMetadataSize(long length)
  {
    if (length < 0 || length > MaximumMetadataBytes) throw Integrity(ManifestKey, "Backup metadata exceeds the configured size limit.");
  }

  static void ValidateInfo(BackupObjectInfo info, string key, long length)
  {
    if (info == null || info.Key != key || info.Length != length || length < 0) throw Integrity(key, "Storage object length or identity mismatch.");
  }
  static bool ValidDigest(string digest) => digest != null && digest.Length == 64 && digest.All(Uri.IsHexDigit);
  static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
  static LiveBackupIntegrityException Integrity(string key, string message) => new(key, message);

  sealed class RestorePin(ObjectStorageLiveBackupProvider provider, string id) : IAsyncDisposable
  {
    public ValueTask DisposeAsync() => new(provider.ReleaseRestorePinAsync(id));
  }

  sealed class ObjectRecordWriter : ILiveBackupRecordWriter
  {
    readonly BackupObjectWrite Upload;
    readonly LiveBackupRecordBatchWriter Writer;
    readonly LiveBackupRecordBatch Batch;
    readonly Action Completed;
    long Count;
    bool Disposed;
    public ObjectRecordWriter(BackupObjectWrite upload, LiveBackupRecordBatch batch, Action completed)
    { Upload = upload; Batch = batch; Completed = completed; Writer = new(upload.Stream, batch, leaveOpen: true); }
    public async Task WriteAsync(LiveBackupRecord record, CancellationToken cancellationToken)
    { await Writer.WriteAsync(record, cancellationToken).ConfigureAwait(false); ++Count; }
    public async ValueTask DisposeAsync()
    {
      if (Disposed) return;
      Disposed = true;
      try
      {
        await Writer.DisposeAsync().ConfigureAwait(false);
        ValidateInfo(await Upload.CommitAsync(CancellationToken.None).ConfigureAwait(false), Batch.StorageKey, Batch.StoredLength);
        Batch.RecordCount = Count;
        Completed();
      }
      finally { await Upload.DisposeAsync().ConfigureAwait(false); }
    }
  }

  sealed class ObjectReadStream(BackupObjectRead read) : Stream
  {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Ownership is atomically transferred to a local and disposed on both disposal paths.")]
    BackupObjectRead Owner = read;
    Stream Inner => Owner.Stream;
    public override bool CanRead => Owner != null && Inner.CanRead;
    public override bool CanSeek => Owner != null && Inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => Inner.Length;
    public override long Position { get => Inner.Position; set => Inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => Inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        var owner = Interlocked.Exchange(ref Owner, null);
        if (owner != null) Task.Run(() => owner.DisposeAsync().AsTask()).GetAwaiter().GetResult();
      }
      base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
      var owner = Interlocked.Exchange(ref Owner, null);
      if (owner != null) await owner.DisposeAsync().ConfigureAwait(false);
      await base.DisposeAsync().ConfigureAwait(false);
    }
  }
}

internal sealed class ObjectBackupState
{
  public int Version { get; set; } = 1;
  public long Revision { get; set; }
  public long LastAllocatedId { get; set; }
  public long CurrentGenerationId { get; set; }
  public long ActiveGenerationId { get; set; }
  public List<ObjectBackupGenerationReference> Generations { get; set; } = [];
  public List<ObjectStorageBackupRestorePin> RestorePins { get; set; } = [];
}

internal sealed class ObjectBackupGenerationReference
{
  public long Id { get; set; }
  public long Length { get; set; }
  public string Sha256 { get; set; }
}

internal sealed class ObjectBackupCatalog
{
  public int Version { get; set; } = 1;
  public long GenerationId { get; set; }
  public LiveBackupTreeKind TreeKind { get; set; }
  public long StartedAtUnixSeconds { get; set; }
  public long CompletedAtUnixSeconds { get; set; }
  public long LastOpIndex { get; set; }
  public List<LiveBackupFile> Files { get; set; } = [];
  public LiveBackupRecordBatch RecordBatch { get; set; }
  [JsonIgnore]
  public bool RecordBatchOpen { get; set; }
}

[JsonSerializable(typeof(ObjectBackupState))]
[JsonSerializable(typeof(ObjectBackupCatalog))]
internal sealed partial class ObjectBackupJsonContext : JsonSerializerContext { }

