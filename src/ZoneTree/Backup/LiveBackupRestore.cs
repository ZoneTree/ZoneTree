using ZoneTree.Core;
using ZoneTree.AbstractFileStream;
using ZoneTree.Options;
using ZoneTree.WAL;

namespace ZoneTree.Backup;

public sealed class LiveBackupRestore<TKey, TValue>
{
  readonly ZoneTreeOptions<TKey, TValue> Options;

  readonly ILiveBackupSource Source;
  readonly LiveBackupTreeKind TreeKind;

  public LiveBackupRestore(
      ZoneTreeOptions<TKey, TValue> options,
      ILiveBackupSource source,
      LiveBackupTreeKind treeKind = LiveBackupTreeKind.Plain)
  {
    Options = options ?? throw new ArgumentNullException(nameof(options));
    Source = source ?? throw new ArgumentNullException(nameof(source));
    TreeKind = treeKind;
  }

  public async ValueTask RestoreLatest()
  {
    await using var generation = await Source.ReadLatestGenerationAsync(CancellationToken.None).ConfigureAwait(false);
    await Restore(generation);
  }

  public async ValueTask RestoreGeneration(long generationId)
  {
    await using var generation = await Source.ReadGenerationAsync(generationId, CancellationToken.None).ConfigureAwait(false);
    await Restore(generation);
  }

  async ValueTask Restore(LiveBackupGeneration generation)
  {
    if (generation.TreeKind != TreeKind)
      throw new LiveBackupIntegrityException($"generation {generation.GenerationId}",
          $"This is a {generation.TreeKind} backup; restore requires the matching plain or transactional API.");
    if (ZoneTreeMetaWAL<TKey, TValue>.Exists(Options))
      throw new LiveBackupRestoreTargetAlreadyExistsException();

    await RestoreDiskSegmentFiles(generation);

    (var readOnlySegments, var maximumSegmentId, var lastOpIndex) = await RestoreInMemoryRecordBatch(
        generation,
        GetMaximumSegmentId(generation));
    var mutableSegmentId = ++maximumSegmentId;
    CreateMutableWal(mutableSegmentId);
    if (Options.WriteAheadLogProvider is WriteAheadLogProvider builtIn)
      builtIn.SyncCategoryDirectory(ZoneTree<TKey, TValue>.SegmentWalCategory);
    var orderedSegmentIds = GetOrderedSegmentIds(generation);
    var diskSegmentId = orderedSegmentIds.Length > 0 ? orderedSegmentIds[0] : 0;
    var bottomSegments = orderedSegmentIds
        .Skip(1)
        .ToArray();

    SaveMetaData(
        lastOpIndex,
        mutableSegmentId,
        diskSegmentId,
        readOnlySegments,
        bottomSegments);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1849:Call async methods when in an async method",
      Justification = "Flush(true) is required for power-loss durability; FlushAsync only publishes bytes to the OS.")]
  async ValueTask RestoreDiskSegmentFiles(
      LiveBackupGeneration generation)
  {
    var dataDirectory = GetTargetDataDirectory();
    var files = Options.RandomAccessDeviceManager.FileStreamProvider;
    files.CreateDirectory(dataDirectory);
    foreach (var file in generation.Files)
    {
      if (string.IsNullOrWhiteSpace(file.FileName) || file.FileName.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(file.FileName))
        throw new LiveBackupIntegrityException(file.FileName, "Expected a segment filename without directories.");
      var destination = Path.Combine(dataDirectory, file.FileName);
      await using var source = await Source.OpenSegmentFileAsync(file, CancellationToken.None).ConfigureAwait(false);
      using var checkedSource = new LiveBackupHashStream(source, leaveOpen: true);
      using var destinationStream = files.CreateFileStream(
          destination,
          FileMode.Create,
          FileAccess.Write,
          FileShare.None);
      await checkedSource.CopyToAsync(destinationStream.ToStream()).ConfigureAwait(false);
      checkedSource.Validate(file.ByteLength, file.Sha256, file.FileName);
      destinationStream.Flush(flushToDisk: true);
    }
    // Metadata and later WAL syncs cannot make these immutable copies durable.
    DirectorySync.FlushTree(dataDirectory, files.SyncDirectory);
  }

  async ValueTask<(long[] readOnlySegments, long maximumSegmentId, long)> RestoreInMemoryRecordBatch(
      LiveBackupGeneration generation, long maximumSegmentId)
  {
    var batch = generation.RecordBatch;
    var restoreWalOptions = CloneWriteAheadLogOptions(
        Options.WriteAheadLogOptions);
    Options.WriteAheadLogProvider.InitCategory(
        ZoneTree<TKey, TValue>.SegmentWalCategory);

    var opIndex = generation.LastOpIndex;
    if (batch == null)
      return ([], maximumSegmentId, opIndex);

    await using var source = await Source.OpenRecordBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
    using var reader = new LiveBackupRecordBatchReader(source, batch, leaveOpen: true);
    if (batch.RecordCount == 0)
    {
      // Even an empty batch must match its checksum, lengths and record count.
      reader.TryRead(out _);
      return ([], maximumSegmentId, opIndex);
    }
    var segmentId = ++maximumSegmentId;
    var wal = Options.WriteAheadLogProvider.GetOrCreateWAL(
        segmentId,
        ZoneTree<TKey, TValue>.SegmentWalCategory,
        restoreWalOptions,
        Options.KeySerializer,
        Options.ValueSerializer);
    try
    {
      while (reader.TryRead(out var record))
      {
        var key = Options.KeySerializer.Deserialize(record.Key.ToArray());
        var value = Options.ValueSerializer.Deserialize(record.Value.ToArray());
        if (record.IsDeleted)
          Options.MarkValueDeleted(ref value);
        wal.Append(in key, in value, ++opIndex);
      }
    }
    finally
    {
      wal.Dispose();
      Options.WriteAheadLogProvider.RemoveWAL(
          segmentId,
          ZoneTree<TKey, TValue>.SegmentWalCategory);
    }
    return ([segmentId], maximumSegmentId, opIndex);
  }

  void CreateMutableWal(long mutableWalSegmentId)
  {
    var restoreWalOptions = CloneWriteAheadLogOptions(
        Options.WriteAheadLogOptions);
    Options.WriteAheadLogProvider.InitCategory(
        ZoneTree<TKey, TValue>.SegmentWalCategory);

    using var mutableWal = Options.WriteAheadLogProvider.GetOrCreateWAL(
        mutableWalSegmentId,
        ZoneTree<TKey, TValue>.SegmentWalCategory,
        restoreWalOptions,
        Options.KeySerializer,
        Options.ValueSerializer);
    Options.WriteAheadLogProvider.RemoveWAL(
        mutableWalSegmentId,
        ZoneTree<TKey, TValue>.SegmentWalCategory);
  }

  void SaveMetaData(long lastOpIndex,
      long mutableSegmentId,
      long diskSegmentId,
      long[] readOnlySegments,
      long[] bottomSegments)
  {
    var meta = new ZoneTreeMeta
    {
      ComparerType = Options.Comparer.GetType().SimplifiedFullName(),
      KeyHasherType = Options.KeyHasher?.GetType().SimplifiedFullName(),
      KeyType = typeof(TKey).SimplifiedFullName(),
      ValueType = typeof(TValue).SimplifiedFullName(),
      KeySerializerType = Options.KeySerializer.GetType().SimplifiedFullName(),
      ValueSerializerType = Options.ValueSerializer.GetType().SimplifiedFullName(),
      MutableSegmentMaxItemCount = Options.MutableSegmentMaxItemCount,
      MutableSegmentBloomFilterBitsPerItem =
          Options.MutableSegmentBloomFilterBitsPerItem,
      DiskSegmentMaxItemCount = Options.DiskSegmentMaxItemCount,
      WriteAheadLogOptions = Options.WriteAheadLogOptions,
      DiskSegmentOptions = Options.DiskSegmentOptions,
      MaximumOpIndex = lastOpIndex,
    };
    using var metaWal = new ZoneTreeMetaWAL<TKey, TValue>(Options, false);
    metaWal.SaveMetaData(
        meta,
        mutableSegmentId,
        diskSegmentId,
        readOnlySegments,
        bottomSegments,
        createNew: true);
  }

  static long GetMaximumSegmentId(
      LiveBackupGeneration generation)
  {
    var maximumSegmentId = 0L;
    foreach (var segmentId in generation.SegmentIds)
      maximumSegmentId = Math.Max(maximumSegmentId, segmentId);
    foreach (var file in generation.Files)
      maximumSegmentId = Math.Max(maximumSegmentId, file.SegmentId);
    return maximumSegmentId;
  }

  static long[] GetOrderedSegmentIds(LiveBackupGeneration generation)
  {
    return [.. generation.Files
        .GroupBy(x => x.Order)
        .OrderBy(g => g.Key)
        .Select(g =>
        {
          var multi = g.FirstOrDefault(x =>
              x.FileName.EndsWith(".multi", StringComparison.Ordinal));

          if (multi is not null)
            return multi.SegmentId;

          return g.First().SegmentId;
        })];
  }

  string GetTargetDataDirectory()
  {
    var metaPath = Options.RandomAccessDeviceManager.GetFilePath(0, ".json");
    return Path.GetDirectoryName(metaPath);
  }

  static WriteAheadLogOptions CloneWriteAheadLogOptions(
      WriteAheadLogOptions options)
  {
    return new WriteAheadLogOptions
    {
      WriteAheadLogMode = options.WriteAheadLogMode,
      CustomOptions = options.CustomOptions,
      CompressionBlockSize = options.CompressionBlockSize,
      CompressionMethod = options.CompressionMethod,
      CompressionLevel = options.CompressionLevel,
      SharedModeOptions = new SharedModeOptions
      {
        MaximumChunkSize = options.SharedModeOptions.MaximumChunkSize
      },
      SyncCompressedModeOptions = new SyncCompressedModeOptions
      {
        EnableTailWriterJob = options.SyncCompressedModeOptions.EnableTailWriterJob,
        TailWriterJobInterval = options.SyncCompressedModeOptions.TailWriterJobInterval
      },
      AsyncCompressedModeOptions = new AsyncCompressedModeOptions
      {
        EmptyQueuePollInterval =
            options.AsyncCompressedModeOptions.EmptyQueuePollInterval
      }
    };
  }
}
