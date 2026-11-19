using System.Text.Json;
using ZoneTree.Core;
using ZoneTree.Hashers;
using ZoneTree.Options;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class ZoneTreeMetaOptionsTests
{
  [Test]
  public void MetadataWithoutHasherAndBloomOptionsUsesDefaults()
  {
    const string json = """
        {
          "Version": "1.0.0"
        }
        """;

    var meta = JsonSerializer.Deserialize<ZoneTreeMeta>(json);

    Assert.That(meta, Is.Not.Null);
    Assert.That(meta.KeyHasherType, Is.Null);
    Assert.That(meta.MutableSegmentBloomFilterBitsPerItem, Is.Zero);
  }

  [Test]
  public void CreatePersistsActiveOptionsInMetadata()
  {
    var dataPath = CreateDataPath();
    var expected = CreateExpectedOptions("created");

    try
    {
      using var zoneTree = OpenConfiguredZoneTree(dataPath, expected);
      zoneTree.Maintenance.SaveMetadata();

      Assert.That(
          zoneTree.Maintenance.CloneOptions().DiskSegmentOptions.MaterializedEntryCacheSize,
          Is.EqualTo(expected.DiskSegmentMaterializedEntryCacheSize));
      Assert.That(
          zoneTree.Maintenance.CloneOptions().DiskSegmentOptions.SearchHintPrefetchSize,
          Is.EqualTo(expected.DiskSegmentSearchHintPrefetchSize));
      AssertMetadataOptions(dataPath, expected);
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void OpenExistingTreePersistsActiveOptionsOverLoadedMetadata()
  {
    var dataPath = CreateDataPath();
    var staleOptions = CreateExpectedOptions("stale");
    var activeOptions = CreateExpectedOptions("active");

    try
    {
      using (var zoneTree = OpenConfiguredZoneTree(dataPath, staleOptions))
      {
        zoneTree.Maintenance.SaveMetadata();
      }

      AssertMetadataOptions(dataPath, staleOptions);

      using (OpenConfiguredZoneTree(dataPath, activeOptions))
      {
        AssertMetadataOptions(dataPath, activeOptions);
      }
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  static IZoneTree<string, string> OpenConfiguredZoneTree(
      string dataPath,
      ExpectedOptions expected)
  {
    return new ZoneTreeFactory<string, string>()
        .SetDataDirectory(dataPath)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(expected.MutableSegmentMaxItemCount)
        .SetMutableSegmentBloomFilterBitsPerItem(
            expected.MutableSegmentBloomFilterBitsPerItem)
        .SetDiskSegmentMaxItemCount(expected.DiskSegmentMaxItemCount)
        .ConfigureWriteAheadLogOptions(options =>
        {
          options.Mode = expected.WriteAheadLogMode;
          options.CustomOptions = expected.WriteAheadLogCustomOptions;
          options.CompressionBlockSize = expected.WriteAheadLogCompressionBlockSize;
          options.CompressionMethod = expected.WriteAheadLogCompressionMethod;
          options.CompressionLevel = expected.WriteAheadLogCompressionLevel;
          options.EmptyQueuePollInterval =
              expected.EmptyQueuePollInterval;
          options.EnableSyncJob =
              expected.EnableSyncJob;
          options.SyncJobInterval =
              expected.SyncJobInterval;
        })
        .ConfigureDiskSegmentOptions(options =>
        {
          options.Mode = expected.DiskSegmentMode;
          options.CompressionBlockSize = expected.DiskSegmentCompressionBlockSize;
          options.CompressionMethod = expected.DiskSegmentCompressionMethod;
          options.CompressionLevel = expected.DiskSegmentCompressionLevel;
          options.MaximumRecordCount = expected.DiskSegmentMaximumRecordCount;
          options.MinimumRecordCount = expected.DiskSegmentMinimumRecordCount;
          options.KeyCacheSize = expected.DiskSegmentKeyCacheSize;
          options.ValueCacheSize = expected.DiskSegmentValueCacheSize;
          options.MaterializedEntryCacheSize = expected.DiskSegmentMaterializedEntryCacheSize;
          options.SearchHintPrefetchSize = expected.DiskSegmentSearchHintPrefetchSize;
          options.KeyCacheRecordLifeTime =
              expected.DiskSegmentKeyCacheRecordLifeTime;
          options.ValueCacheRecordLifeTime =
              expected.DiskSegmentValueCacheRecordLifeTime;
          options.DefaultSparseArrayStepSize = expected.DefaultSparseArrayStepSize;
        })
        .OpenOrCreate();
  }

  static ExpectedOptions CreateExpectedOptions(
      string customOptions)
  {
    return customOptions switch
    {
      "stale" => new ExpectedOptions(
          MutableSegmentMaxItemCount: 37,
          MutableSegmentBloomFilterBitsPerItem: 5,
          DiskSegmentMaxItemCount: 73,
          WriteAheadLogMode: WriteAheadLogMode.AsyncPrivate,
          WriteAheadLogCustomOptions: customOptions,
          WriteAheadLogCompressionBlockSize: 128 * 1024,
          WriteAheadLogCompressionMethod: CompressionMethod.None,
          WriteAheadLogCompressionLevel: 0,
          EmptyQueuePollInterval: TimeSpan.FromMilliseconds(17),
          EnableSyncJob: false,
          SyncJobInterval: TimeSpan.FromMilliseconds(19),
          DiskSegmentMode: DiskSegmentMode.SingleDiskSegment,
          DiskSegmentCompressionBlockSize: 256 * 1024,
          DiskSegmentCompressionMethod: CompressionMethod.None,
          DiskSegmentCompressionLevel: 0,
          DiskSegmentMaximumRecordCount: 101,
          DiskSegmentMinimumRecordCount: 51,
          DiskSegmentKeyCacheSize: 11,
          DiskSegmentValueCacheSize: 13,
          DiskSegmentMaterializedEntryCacheSize: 17,
          DiskSegmentSearchHintPrefetchSize: 19,
          DiskSegmentKeyCacheRecordLifeTime: TimeSpan.FromMilliseconds(23),
          DiskSegmentValueCacheRecordLifeTime: TimeSpan.FromMilliseconds(29),
          DefaultSparseArrayStepSize: 31),

      "active" => new ExpectedOptions(
          MutableSegmentMaxItemCount: 41,
          MutableSegmentBloomFilterBitsPerItem: 7,
          DiskSegmentMaxItemCount: 83,
          WriteAheadLogMode: WriteAheadLogMode.Private,
          WriteAheadLogCustomOptions: customOptions,
          WriteAheadLogCompressionBlockSize: 192 * 1024,
          WriteAheadLogCompressionMethod: CompressionMethod.Zstd,
          WriteAheadLogCompressionLevel: CompressionLevels.Zstd1,
          EmptyQueuePollInterval: TimeSpan.FromMilliseconds(43),
          EnableSyncJob: true,
          SyncJobInterval: TimeSpan.FromMilliseconds(47),
          DiskSegmentMode: DiskSegmentMode.MultiPartDiskSegment,
          DiskSegmentCompressionBlockSize: 384 * 1024,
          DiskSegmentCompressionMethod: CompressionMethod.Zstd,
          DiskSegmentCompressionLevel: CompressionLevels.Zstd2,
          DiskSegmentMaximumRecordCount: 107,
          DiskSegmentMinimumRecordCount: 53,
          DiskSegmentKeyCacheSize: 17,
          DiskSegmentValueCacheSize: 19,
          DiskSegmentMaterializedEntryCacheSize: 23,
          DiskSegmentSearchHintPrefetchSize: 29,
          DiskSegmentKeyCacheRecordLifeTime: TimeSpan.FromMilliseconds(31),
          DiskSegmentValueCacheRecordLifeTime: TimeSpan.FromMilliseconds(37),
          DefaultSparseArrayStepSize: 41),

      "created" => new ExpectedOptions(
          MutableSegmentMaxItemCount: 43,
          MutableSegmentBloomFilterBitsPerItem: 11,
          DiskSegmentMaxItemCount: 89,
          WriteAheadLogMode: WriteAheadLogMode.Private,
          WriteAheadLogCustomOptions: customOptions,
          WriteAheadLogCompressionBlockSize: 224 * 1024,
          WriteAheadLogCompressionMethod: CompressionMethod.Zstd,
          WriteAheadLogCompressionLevel: CompressionLevels.Zstd3,
          EmptyQueuePollInterval: TimeSpan.FromMilliseconds(59),
          EnableSyncJob: false,
          SyncJobInterval: TimeSpan.FromMilliseconds(61),
          DiskSegmentMode: DiskSegmentMode.MultiPartDiskSegment,
          DiskSegmentCompressionBlockSize: 512 * 1024,
          DiskSegmentCompressionMethod: CompressionMethod.Zstd,
          DiskSegmentCompressionLevel: CompressionLevels.ZstdMax,
          DiskSegmentMaximumRecordCount: 109,
          DiskSegmentMinimumRecordCount: 59,
          DiskSegmentKeyCacheSize: 23,
          DiskSegmentValueCacheSize: 29,
          DiskSegmentMaterializedEntryCacheSize: 31,
          DiskSegmentSearchHintPrefetchSize: 37,
          DiskSegmentKeyCacheRecordLifeTime: TimeSpan.FromMilliseconds(41),
          DiskSegmentValueCacheRecordLifeTime: TimeSpan.FromMilliseconds(43),
          DefaultSparseArrayStepSize: 47),

      _ => throw new ArgumentOutOfRangeException(nameof(customOptions))
    };
  }

  static void AssertMetadataOptions(
      string dataPath,
      ExpectedOptions expected)
  {
    var metaFilePath = Path.Combine(dataPath, "0.json");
    var json = File.ReadAllText(metaFilePath);
    var meta = JsonSerializer.Deserialize<ZoneTreeMeta>(json);

    Assert.That(meta, Is.Not.Null);
    Assert.That(
        meta.KeyHasherType,
        Is.EqualTo(typeof(DefaultKeyHasher<string>).SimplifiedFullName()));
    Assert.That(meta.MutableSegmentMaxItemCount, Is.EqualTo(expected.MutableSegmentMaxItemCount));
    Assert.That(
        meta.MutableSegmentBloomFilterBitsPerItem,
        Is.EqualTo(expected.MutableSegmentBloomFilterBitsPerItem));
    Assert.That(meta.DiskSegmentMaxItemCount, Is.EqualTo(expected.DiskSegmentMaxItemCount));

    var walOptions = meta.WriteAheadLogOptions;
    Assert.That(walOptions, Is.Not.Null);
    Assert.That(walOptions.Mode, Is.EqualTo(expected.WriteAheadLogMode));
    Assert.That(walOptions.CompressionBlockSize, Is.EqualTo(expected.WriteAheadLogCompressionBlockSize));
    Assert.That(walOptions.CompressionMethod, Is.EqualTo(expected.WriteAheadLogCompressionMethod));
    Assert.That(walOptions.CompressionLevel, Is.EqualTo(expected.WriteAheadLogCompressionLevel));
    Assert.That(walOptions.EmptyQueuePollInterval,
        Is.EqualTo(expected.EmptyQueuePollInterval));
    Assert.That(walOptions.EnableSyncJob,
        Is.EqualTo(expected.EnableSyncJob));
    Assert.That(walOptions.SyncJobInterval,
        Is.EqualTo(expected.SyncJobInterval));

    var diskOptions = meta.DiskSegmentOptions;
    Assert.That(diskOptions, Is.Not.Null);
    Assert.That(diskOptions.Mode, Is.EqualTo(expected.DiskSegmentMode));
    Assert.That(diskOptions.CompressionBlockSize, Is.EqualTo(expected.DiskSegmentCompressionBlockSize));
    Assert.That(diskOptions.CompressionMethod, Is.EqualTo(expected.DiskSegmentCompressionMethod));
    Assert.That(diskOptions.CompressionLevel, Is.EqualTo(expected.DiskSegmentCompressionLevel));
    Assert.That(diskOptions.MaximumRecordCount, Is.EqualTo(expected.DiskSegmentMaximumRecordCount));
    Assert.That(diskOptions.MinimumRecordCount, Is.EqualTo(expected.DiskSegmentMinimumRecordCount));
    Assert.That(diskOptions.KeyCacheSize, Is.EqualTo(expected.DiskSegmentKeyCacheSize));
    Assert.That(diskOptions.ValueCacheSize, Is.EqualTo(expected.DiskSegmentValueCacheSize));
    Assert.That(diskOptions.MaterializedEntryCacheSize,
        Is.EqualTo(expected.DiskSegmentMaterializedEntryCacheSize));
    Assert.That(diskOptions.SearchHintPrefetchSize,
        Is.EqualTo(expected.DiskSegmentSearchHintPrefetchSize));
    Assert.That(diskOptions.KeyCacheRecordLifeTime,
        Is.EqualTo(expected.DiskSegmentKeyCacheRecordLifeTime));
    Assert.That(diskOptions.ValueCacheRecordLifeTime,
        Is.EqualTo(expected.DiskSegmentValueCacheRecordLifeTime));
    Assert.That(diskOptions.DefaultSparseArrayStepSize,
        Is.EqualTo(expected.DefaultSparseArrayStepSize));

    using var document = JsonDocument.Parse(json);
    Assert.That(
        document.RootElement.GetProperty(nameof(ZoneTreeMeta.KeyHasherType)).GetString(),
        Is.EqualTo(typeof(DefaultKeyHasher<string>).SimplifiedFullName()));
    Assert.That(
        document.RootElement
            .GetProperty(nameof(ZoneTreeMeta.MutableSegmentBloomFilterBitsPerItem))
            .GetInt32(),
        Is.EqualTo(expected.MutableSegmentBloomFilterBitsPerItem));
    var persistedCustomOptions = document.RootElement
        .GetProperty(nameof(ZoneTreeMeta.WriteAheadLogOptions))
        .GetProperty(nameof(WriteAheadLogOptions.CustomOptions))
        .GetString();
    Assert.That(persistedCustomOptions, Is.EqualTo(expected.WriteAheadLogCustomOptions));
  }

  static string CreateDataPath()
  {
    return Path.Combine(
        "data",
        $"{nameof(ZoneTreeMetaOptionsTests)}_{Guid.NewGuid():N}");
  }

  static void DeleteDataPath(string dataPath)
  {
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
  }

  sealed record ExpectedOptions(
      int MutableSegmentMaxItemCount,
      int MutableSegmentBloomFilterBitsPerItem,
      int DiskSegmentMaxItemCount,
      WriteAheadLogMode WriteAheadLogMode,
      string WriteAheadLogCustomOptions,
      int WriteAheadLogCompressionBlockSize,
      CompressionMethod WriteAheadLogCompressionMethod,
      int WriteAheadLogCompressionLevel,
      TimeSpan EmptyQueuePollInterval,
      bool EnableSyncJob,
      TimeSpan SyncJobInterval,
      DiskSegmentMode DiskSegmentMode,
      int DiskSegmentCompressionBlockSize,
      CompressionMethod DiskSegmentCompressionMethod,
      int DiskSegmentCompressionLevel,
      int DiskSegmentMaximumRecordCount,
      int DiskSegmentMinimumRecordCount,
      int DiskSegmentKeyCacheSize,
      int DiskSegmentValueCacheSize,
      int DiskSegmentMaterializedEntryCacheSize,
      int DiskSegmentSearchHintPrefetchSize,
      TimeSpan DiskSegmentKeyCacheRecordLifeTime,
      TimeSpan DiskSegmentValueCacheRecordLifeTime,
      int DefaultSparseArrayStepSize);
}
