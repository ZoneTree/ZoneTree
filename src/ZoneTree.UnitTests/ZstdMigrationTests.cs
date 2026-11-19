using ZoneTree.Compression;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Segments.MultiPart;
using static ZoneTree.UnitTests.DbVersionTests;

namespace ZoneTree.UnitTests;

/// <summary>
/// The factory migrates a database from LZ4, Gzip and Brotli compression to Zstd
/// when it opens, after it backs up the database.
/// </summary>
public sealed class ZstdMigrationTests
{
  const string BackupSuffix = ".zstd-migration-backup-";

  static string CreateDataPath() =>
      Path.Combine("data", $"{nameof(ZstdMigrationTests)}_{Guid.NewGuid():N}");

  static string[] GetBackups(string dataPath) =>
      Directory.GetDirectories(
          Path.GetDirectoryName(dataPath),
          Path.GetFileName(dataPath) + BackupSuffix + "*");

  static void DeleteDataPath(string dataPath)
  {
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    foreach (var backup in GetBackups(dataPath))
      Directory.Delete(backup, true);
  }

  static void Insert(IZoneTree<int, int> tree, int from, int to)
  {
    for (var i = from; i < to; ++i)
      tree.Upsert(i, i + 1);
  }

  static void AssertRecords(IZoneTree<int, int> tree, int count)
  {
    for (var i = 0; i < count; ++i)
    {
      Assert.That(tree.TryGet(i, out var value), Is.True, $"key {i}");
      Assert.That(value, Is.EqualTo(i + 1));
    }
  }

  [Test]
  public void MigrationConvertsEveryLegacyFile(
      [Values(WriteAheadLogMode.SyncCompressed, WriteAheadLogMode.AsyncCompressed, WriteAheadLogMode.Shared)]
      WriteAheadLogMode walMode,
      [Values] DiskSegmentMode diskSegmentMode)
  {
    var dataPath = CreateDataPath();
    try
    {
      // Records in the disk segment, in a read-only segment and in the mutable segment.
      using (var tree = CreateFactory(
              dataPath, SilentTestLogger.Instance, walMode,
              walMethod: CompressionMethod.LZ4, diskMethod: CompressionMethod.LZ4)
          .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = diskSegmentMode)
          .OpenOrCreate())
      {
        Insert(tree, 0, 100);
        MergeToDisk(tree);
        Insert(tree, 100, 150);
        tree.Maintenance.MoveMutableSegmentForward();
        Insert(tree, 150, 200);
      }

      var logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger, walMode)
          .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = diskSegmentMode)
          .EnableZstdMigration()
          .OpenOrCreate())
        AssertRecords(tree, 200);
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
      Assert.That(logger.GetWarnings(), Has.One.Contains(BackupSuffix));
      Assert.That(logger.GetWarnings(), Has.None.Contains("2.2.0 removes"));
      var backup = GetBackups(dataPath).Single();
      Assert.That(File.Exists(Path.Combine(backup, "0.json")), Is.True);

      // The converted files take new writes and merges.
      logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger, walMode)
          .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = diskSegmentMode)
          .OpenOrCreate())
      {
        AssertRecords(tree, 200);
        Insert(tree, 200, 300);
        MergeToDisk(tree);
        AssertRecords(tree, 300);
      }
      using (var tree = CreateFactory(dataPath, logger, walMode)
          .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = diskSegmentMode)
          .OpenOrCreate())
        AssertRecords(tree, 300);
      Assert.That(logger.GetWarnings(), Is.Empty);
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void MigrationConvertsSyncWals(
      [Values(WriteAheadLogMode.SyncCompressed, WriteAheadLogMode.AsyncCompressed, WriteAheadLogMode.Shared, WriteAheadLogMode.None)]
      WriteAheadLogMode walMode)
  {
    var dataPath = CreateDataPath();
    try
    {
      // Records in the disk segment, in a read-only segment and in the mutable segment.
      using (var tree = CreateFactory(dataPath, SilentTestLogger.Instance, WriteAheadLogMode.Sync).OpenOrCreate())
      {
        Insert(tree, 0, 100);
        MergeToDisk(tree);
        Insert(tree, 100, 150);
        tree.Maintenance.MoveMutableSegmentForward();
        Insert(tree, 150, 200);
      }

      var logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger, walMode).EnableZstdMigration().OpenOrCreate())
        AssertRecords(tree, 200);
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
      Assert.That(logger.GetWarnings(), Has.None.Contains("2.2.0 removes"));
      Assert.That(Directory.GetFiles(dataPath, "*.wal_xxh3.0", SearchOption.AllDirectories), Is.Empty);

      // The converted WALs take new writes and merges.
      logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger, walMode).OpenOrCreate())
      {
        AssertRecords(tree, 200);
        Insert(tree, 200, 300);
        MergeToDisk(tree);
      }
      using (var tree = CreateFactory(dataPath, logger, walMode).OpenOrCreate())
        AssertRecords(tree, 300);
      Assert.That(logger.GetWarnings(), Is.Empty);
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void MigrationNeedsAWalModeOtherThanSync()
  {
    var dataPath = CreateDataPath();
    try
    {
      using (var tree = CreateFactory(dataPath, SilentTestLogger.Instance, WriteAheadLogMode.Sync).OpenOrCreate())
        Insert(tree, 0, 100);

      var factory = CreateFactory(dataPath, SilentTestLogger.Instance, WriteAheadLogMode.Sync).EnableZstdMigration();
      Assert.Throws<InvalidOptionValueException>(() => factory.OpenOrCreate().Dispose());
      Assert.That(GetBackups(dataPath), Is.Empty);
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void MigrationConvertsALegacyMultipartHeader()
  {
    var dataPath = CreateDataPath();
    try
    {
      using (var tree = CreateFactory(dataPath, SilentTestLogger.Instance)
          .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = DiskSegmentMode.MultiPartDiskSegment)
          .OpenOrCreate())
      {
        Insert(tree, 0, 100);
        MergeToDisk(tree);
      }
      // A header from before the envelope, in a database from before 2.1.10.
      var header = Directory.GetFiles(dataPath, "*.multi").Single();
      var bytes = MultiPartMetadataCodec.Decode(File.ReadAllBytes(header), out _);
      File.WriteAllBytes(header, LZ4DataCompression.Compress(bytes, CompressionLevels.LZ4Fastest).ToArray());
      RemoveDbVersion(dataPath);

      var logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger)
          .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = DiskSegmentMode.MultiPartDiskSegment)
          .OpenOrCreate())
        AssertRecords(tree, 100);
      Assert.That(ReadDbVersion(dataPath), Is.Null);
      Assert.That(logger.GetWarnings(), Has.One.Contains(Path.GetFileName(header)));

      using (var tree = CreateFactory(dataPath, SilentTestLogger.Instance)
          .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = DiskSegmentMode.MultiPartDiskSegment)
          .EnableZstdMigration()
          .OpenOrCreate())
        AssertRecords(tree, 100);
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
      MultiPartMetadataCodec.Decode(File.ReadAllBytes(header), out var method);
      Assert.That(method, Is.EqualTo(CompressionMethod.Zstd));
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void MigrationOfAMigratedDatabaseDoesNothing()
  {
    var dataPath = CreateDataPath();
    try
    {
      using (var tree = CreateFactory(dataPath, SilentTestLogger.Instance).OpenOrCreate())
        Insert(tree, 0, 100);

      var logger = new RecordingLogger();
      using (var tree = CreateFactory(dataPath, logger).EnableZstdMigration().OpenOrCreate())
        AssertRecords(tree, 100);
      Assert.That(GetBackups(dataPath), Is.Empty);
      Assert.That(logger.GetWarnings(), Is.Empty);
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void MigrationNeedsZstdOrNoCompression()
  {
    var dataPath = CreateDataPath();
    try
    {
      using (var tree = CreateFactory(
          dataPath, SilentTestLogger.Instance, walMethod: CompressionMethod.LZ4).OpenOrCreate())
        Insert(tree, 0, 100);

      var factory = CreateFactory(dataPath, SilentTestLogger.Instance, walMethod: CompressionMethod.LZ4)
          .EnableZstdMigration();
      Assert.Throws<InvalidOptionValueException>(() => factory.OpenOrCreate().Dispose());
      Assert.That(GetBackups(dataPath), Is.Empty);
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }
}
