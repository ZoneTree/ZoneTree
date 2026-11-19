using System.Text.Json;
using System.Text.Json.Nodes;
using ZoneTree.AbstractFileStream;
using ZoneTree.Compression;
using ZoneTree.Core;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments.MultiPart;
using ZoneTree.Segments.RandomAccess;

namespace ZoneTree.UnitTests;

/// <summary>
/// A database reaches the current storage format version once nothing in it
/// uses LZ4, Gzip or Brotli compression, which version 2.2.0 removes.
/// </summary>
public sealed class DbVersionTests
{
  internal const string CurrentDbVersion = "2.1.10";

  internal const int RecordCount = 100;

  internal sealed class RecordingLogger : ILogger
  {
    readonly List<string> Warnings = new();

    public LogLevel LogLevel { get; set; } = LogLevel.Warning;

    public string[] GetWarnings()
    {
      lock (Warnings)
        return Warnings.ToArray();
    }

    public void LogError(Exception log) { }

    public void LogWarning(object log)
    {
      lock (Warnings)
        Warnings.Add(log.ToString());
    }

    public void LogInfo(object log) { }

    public void LogTrace(object log) { }
  }

  internal static ZoneTreeFactory<int, int> CreateFactory(
      string dataPath,
      ILogger logger,
      WriteAheadLogMode walMode = WriteAheadLogMode.SyncCompressed,
      CompressionMethod walMethod = CompressionMethod.Zstd,
      CompressionMethod diskMethod = CompressionMethod.Zstd)
  {
    return new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetLogger(logger)
        .ConfigureWriteAheadLogOptions(x =>
        {
          x.WriteAheadLogMode = walMode;
          x.CompressionMethod = walMethod;
        })
        .ConfigureDiskSegmentOptions(x => x.CompressionMethod = diskMethod);
  }

  internal static void Insert(IZoneTree<int, int> tree)
  {
    for (var i = 0; i < RecordCount; ++i)
      tree.Upsert(i, i + 1);
  }

  internal static void MergeToDisk(IZoneTree<int, int> tree)
  {
    tree.Maintenance.MoveMutableSegmentForward();
    tree.Maintenance.StartMergeOperation()?.Join();
  }

  internal static void AssertRecords(IZoneTree<int, int> tree)
  {
    for (var i = 0; i < RecordCount; ++i)
    {
      Assert.That(tree.TryGet(i, out var value), Is.True, $"key {i}");
      Assert.That(value, Is.EqualTo(i + 1));
    }
  }

  internal static string MetaFilePath(string dataPath) => Path.Combine(dataPath, "0.json");

  internal static string ReadDbVersion(string dataPath) =>
      JsonSerializer.Deserialize<ZoneTreeMeta>(File.ReadAllText(MetaFilePath(dataPath))).DbVersion;

  /// <summary>
  /// Makes the database look like one that a version before 2.1.10 saved.
  /// </summary>
  internal static void RemoveDbVersion(string dataPath)
  {
    var meta = JsonNode.Parse(File.ReadAllText(MetaFilePath(dataPath))).AsObject();
    meta.Remove(nameof(ZoneTreeMeta.DbVersion));
    File.WriteAllText(MetaFilePath(dataPath), meta.ToJsonString());
  }

  static string CreateDataPath() =>
      Path.Combine("data", $"{nameof(DbVersionTests)}_{Guid.NewGuid():N}");

  static void DeleteDataPath(string dataPath)
  {
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
  }

  [Test]
  public void NewTreeKeepsTheCurrentDbVersionThroughSavesAndMerges()
  {
    var dataPath = CreateDataPath();
    var logger = new RecordingLogger();
    try
    {
      using (var tree = CreateFactory(dataPath, logger).OpenOrCreate())
      {
        Insert(tree);
        MergeToDisk(tree);
      }
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
      using (var tree = CreateFactory(dataPath, logger).OpenOrCreate())
        AssertRecords(tree);
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
      Assert.That(logger.GetWarnings(), Is.Empty);
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void NewTreeWithLegacyCompressionHasNoDbVersionAndWarns()
  {
    var dataPath = CreateDataPath();
    var logger = new RecordingLogger();
    try
    {
      using (var tree = CreateFactory(dataPath, logger, walMethod: CompressionMethod.LZ4).OpenOrCreate())
        Insert(tree);
      Assert.That(ReadDbVersion(dataPath), Is.Null);
      Assert.That(
          logger.GetWarnings(),
          Has.One.Contains("WriteAheadLogOptions.CompressionMethod = LZ4"));
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void OldTreeFreeOfLegacyCompressionMovesToTheCurrentDbVersion()
  {
    var dataPath = CreateDataPath();
    var logger = new RecordingLogger();
    try
    {
      using (var tree = CreateFactory(dataPath, logger).OpenOrCreate())
      {
        Insert(tree);
        MergeToDisk(tree);
      }
      RemoveDbVersion(dataPath);
      Assert.That(ReadDbVersion(dataPath), Is.Null);

      using (var tree = CreateFactory(dataPath, logger).OpenOrCreate())
        AssertRecords(tree);
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
      Assert.That(logger.GetWarnings(), Is.Empty);
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public void LegacyWalKeepsTheOldDbVersionUntilAMergeDropsIt(WriteAheadLogMode walMode)
  {
    var dataPath = CreateDataPath();
    try
    {
      using (var tree = CreateFactory(
          dataPath, SilentTestLogger.Instance, walMode, walMethod: CompressionMethod.LZ4).OpenOrCreate())
        Insert(tree);

      // The WAL of the mutable segment keeps the method of its file.
      var logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger, walMode).OpenOrCreate())
      {
        Assert.That(ReadDbVersion(dataPath), Is.Null);
        Assert.That(logger.GetWarnings(), Has.One.Contains(dataPath));
        MergeToDisk(tree);
      }

      logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger, walMode).OpenOrCreate())
        AssertRecords(tree);
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
      Assert.That(logger.GetWarnings(), Is.Empty);
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.Shared)]
  public void LegacyWalOfAReadOnlySegmentKeepsTheOldDbVersion(WriteAheadLogMode walMode)
  {
    var dataPath = CreateDataPath();
    try
    {
      using (var tree = CreateFactory(
          dataPath, SilentTestLogger.Instance, walMode, walMethod: CompressionMethod.LZ4).OpenOrCreate())
        Insert(tree);
      // The legacy WAL becomes read-only, and no merge drops it. The next mutable
      // segment writes a Zstd WAL, so only the read-only segment uses LZ4.
      using (var tree = CreateFactory(dataPath, SilentTestLogger.Instance, walMode).OpenOrCreate())
        tree.Maintenance.MoveMutableSegmentForward();

      var logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger, walMode).OpenOrCreate())
        AssertRecords(tree);
      Assert.That(ReadDbVersion(dataPath), Is.Null);
      Assert.That(logger.GetWarnings(), Has.One.Contains(dataPath));
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void LegacyDiskSegmentKeepsTheOldDbVersion([Values] DiskSegmentMode diskSegmentMode)
  {
    var dataPath = CreateDataPath();
    try
    {
      using (var tree = CreateFactory(dataPath, SilentTestLogger.Instance, diskMethod: CompressionMethod.LZ4)
          .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = diskSegmentMode)
          .OpenOrCreate())
      {
        Insert(tree);
        MergeToDisk(tree);
      }

      var logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger)
          .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = diskSegmentMode)
          .OpenOrCreate())
        AssertRecords(tree);
      Assert.That(ReadDbVersion(dataPath), Is.Null);
      Assert.That(logger.GetWarnings(), Has.One.Contains(dataPath));
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void TreeAtTheCurrentDbVersionRejectsLegacyCompression([Values] bool forWal)
  {
    var dataPath = CreateDataPath();
    try
    {
      using (var tree = CreateFactory(dataPath, SilentTestLogger.Instance).OpenOrCreate())
        Insert(tree);

      var factory = forWal
          ? CreateFactory(dataPath, SilentTestLogger.Instance, walMethod: CompressionMethod.LZ4)
          : CreateFactory(dataPath, SilentTestLogger.Instance, diskMethod: CompressionMethod.LZ4);
      var exception = Assert.Throws<InvalidOptionValueException>(() => factory.OpenOrCreate().Dispose());
      Assert.That(exception.Value, Is.EqualTo(CompressionMethod.LZ4));
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  /// <summary>
  /// Rewrites a compressed disk file with another compression method.
  /// </summary>
  static void RecompressDiskFile(string path, CompressionMethod method)
  {
    var provider = new LocalFileStreamProvider();
    var deviceManager = new RandomAccessDeviceManager(SilentTestLogger.Instance, provider, Path.GetDirectoryName(path));
    var rewritten = path + ".rewritten";
    using (var source = new CompressedFileRandomAccessDevice(
        SilentTestLogger.Instance, provider, 0, path, deviceManager, path, false, 0, method, 0))
    using (var target = new CompressedFileRandomAccessDevice(
        SilentTestLogger.Instance, provider, 0, rewritten, deviceManager, rewritten, true, source.BlockSize, method, 0))
    {
      target.AppendBytesReturnPosition(source.GetBytes(0, (int)source.Length).Span);
      target.SealDevice();
    }
    File.Move(rewritten, path, true);
  }

  [Test]
  public void LegacySparseArrayKeepsTheOldDbVersion([Values] DiskSegmentMode diskSegmentMode)
  {
    var dataPath = CreateDataPath();
    try
    {
      using (var tree = CreateFactory(dataPath, SilentTestLogger.Instance)
          .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = diskSegmentMode)
          .OpenOrCreate())
      {
        Insert(tree);
        MergeToDisk(tree);
      }
      // The load closes a sparse array after it reads it.
      var sparseArray = Directory.GetFiles(dataPath, "*.sparse.z").Single();
      RecompressDiskFile(sparseArray, CompressionMethod.LZ4);
      RemoveDbVersion(dataPath);

      var logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger)
          .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = diskSegmentMode)
          .OpenOrCreate())
        AssertRecords(tree);
      Assert.That(ReadDbVersion(dataPath), Is.Null);
      Assert.That(logger.GetWarnings(), Has.One.Contains(Path.GetFileName(sparseArray)));
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void SyncWalKeepsTheOldDbVersion()
  {
    var dataPath = CreateDataPath();
    try
    {
      var logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger, WriteAheadLogMode.Sync).OpenOrCreate())
        Insert(tree);
      Assert.That(ReadDbVersion(dataPath), Is.Null);
      Assert.That(logger.GetWarnings(), Has.One.Contains("WriteAheadLogOptions.WriteAheadLogMode = Sync"));

      // The WAL of the mutable segment keeps its mode.
      logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger, WriteAheadLogMode.SyncCompressed).OpenOrCreate())
        AssertRecords(tree);
      Assert.That(ReadDbVersion(dataPath), Is.Null);
      Assert.That(logger.GetWarnings(), Has.One.Contains(".wal_xxh3.0"));
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void TreeAtTheCurrentDbVersionRejectsTheSyncWalMode()
  {
    var dataPath = CreateDataPath();
    try
    {
      using (var tree = CreateFactory(dataPath, SilentTestLogger.Instance).OpenOrCreate())
        Insert(tree);

      var factory = CreateFactory(dataPath, SilentTestLogger.Instance, WriteAheadLogMode.Sync);
      var exception = Assert.Throws<InvalidOptionValueException>(() => factory.OpenOrCreate().Dispose());
      Assert.That(exception.Value, Is.EqualTo(WriteAheadLogMode.Sync));
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void WalModeNoneDoesNotUseItsCompressionMethod()
  {
    var dataPath = CreateDataPath();
    var logger = new RecordingLogger();
    try
    {
      using (var tree = CreateFactory(dataPath, logger, WriteAheadLogMode.None, walMethod: CompressionMethod.LZ4).OpenOrCreate())
        Insert(tree);
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
      using (var tree = CreateFactory(dataPath, logger, WriteAheadLogMode.None, walMethod: CompressionMethod.LZ4).OpenOrCreate())
        tree.Upsert(RecordCount, 0);
      Assert.That(logger.GetWarnings(), Is.Empty);
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void MultipartMetadataReportsItsCompressionMethod()
  {
    var bytes = Enumerable.Range(0, 1000).Select(i => (byte)(i % 7)).ToArray();

    var decoded = MultiPartMetadataCodec.Decode(MultiPartMetadataCodec.Encode(bytes), out var method);
    Assert.That(method, Is.EqualTo(CompressionMethod.Zstd));
    Assert.That(decoded.ToArray(), Is.EqualTo(bytes));

    // Metadata from before the envelope is LZ4.
    decoded = MultiPartMetadataCodec.Decode(
        LZ4DataCompression.Compress(bytes, CompressionLevels.LZ4Fastest), out method);
    Assert.That(method, Is.EqualTo(CompressionMethod.LZ4));
    Assert.That(decoded.ToArray(), Is.EqualTo(bytes));
  }
}
