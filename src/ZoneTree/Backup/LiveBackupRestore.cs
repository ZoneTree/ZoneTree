using ZoneTree.Core;
using ZoneTree.Options;
using ZoneTree.WAL;

namespace ZoneTree.Backup;

public sealed class LiveBackupRestore<TKey, TValue>
{
  readonly ZoneTreeOptions<TKey, TValue> Options;

  readonly ILiveBackupSource Source;

  public LiveBackupRestore(
      ZoneTreeOptions<TKey, TValue> options,
      ILiveBackupSource source)
  {
    Options = options ?? throw new ArgumentNullException(nameof(options));
    Source = source ?? throw new ArgumentNullException(nameof(source));
  }

  public async ValueTask RestoreLatest()
  {
    var generation = await Source.ReadLatestGenerationAsync(CancellationToken.None);
    await Restore(generation);
  }

  public async ValueTask RestoreGeneration(long generationId)
  {
    var generation = await Source.ReadGenerationAsync(generationId, CancellationToken.None);
    await Restore(generation);
  }

  async ValueTask Restore(LiveBackupGeneration generation)
  {
    if (ZoneTreeMetaWAL<TKey, TValue>.Exists(Options))
      throw new LiveBackupRestoreTargetAlreadyExistsException();

    await RestoreDiskSegmentFiles(generation);

    (var readOnlySegments, var maximumSegmentId, var lastOpIndex) = await RestoreInMemoryRecordBatch(
        generation,
        GetMaximumSegmentId(generation));
    var mutableSegmentId = ++maximumSegmentId;
    CreateMutableWal(mutableSegmentId);
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

  async ValueTask RestoreDiskSegmentFiles(
      LiveBackupGeneration generation)
  {
    var dataDirectory = GetTargetDataDirectory();
    Directory.CreateDirectory(dataDirectory);
    foreach (var file in generation.Files)
    {
      var destination = Path.Combine(dataDirectory, file.FileName);
      using var source = await Source.OpenSegmentFileAsync(file, CancellationToken.None);
      using var destinationStream = File.Open(
          destination,
          FileMode.Create,
          FileAccess.Write,
          FileShare.None);
      await source.CopyToAsync(destinationStream);
    }
  }

  async ValueTask<(long[] readOnlySegments, long maximumSegmentId, long)> RestoreInMemoryRecordBatch(
      LiveBackupGeneration generation, long maximumSegmentId)
  {
    var batch = generation.RecordBatch;
    var hasInMemoryRecordBatch = batch != null && batch.RecordCount > 0;
    var restoreWalOptions = CloneWriteAheadLogOptions(
        Options.WriteAheadLogOptions);
    Options.WriteAheadLogProvider.InitCategory(
        ZoneTree<TKey, TValue>.SegmentWalCategory);

    var opIndex = generation.LastOpIndex;
    if (!hasInMemoryRecordBatch)
      return ([], maximumSegmentId, opIndex);

    var segmentId = ++maximumSegmentId;
    using var source = await Source.OpenRecordBatchAsync(batch, CancellationToken.None);
    using var reader = new LiveBackupRecordBatchReader(source, batch);
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
