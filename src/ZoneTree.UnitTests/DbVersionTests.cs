using System.Text.Json;
using System.Text.Json.Nodes;
using ZoneTree.AbstractFileStream;
using ZoneTree.Backup;
using ZoneTree.Core;
using ZoneTree.Exceptions;

namespace ZoneTree.UnitTests;

/// <summary>
/// ZoneTree opens databases that version 2.1.10 migrated, and moves them to the current DbVersion.
/// </summary>
public sealed class DbVersionTests
{
  const int RecordCount = 100;

  static readonly string CurrentDbVersion = ZoneTreeInfo.DbVersion.ToString();

  static ZoneTreeFactory<int, int> CreateFactory(string dataPath) =>
      new ZoneTreeFactory<int, int>()
          .SetDataDirectory(dataPath)
          .SetLogger(SilentTestLogger.Instance);

  static void Insert(IZoneTree<int, int> tree)
  {
    for (var i = 0; i < RecordCount; ++i)
      tree.Upsert(i, i + 1);
  }

  static void AssertRecords(IZoneTree<int, int> tree)
  {
    for (var i = 0; i < RecordCount; ++i)
    {
      Assert.That(tree.TryGet(i, out var value), Is.True, $"key {i}");
      Assert.That(value, Is.EqualTo(i + 1));
    }
  }

  static void CreateTree(string dataPath)
  {
    using var tree = CreateFactory(dataPath).OpenOrCreate();
    Insert(tree);
    tree.Maintenance.MoveMutableSegmentForward();
    tree.Maintenance.StartMergeOperation()?.Join();
    Insert(tree);
  }

  static string MetaFilePath(string dataPath) => Path.Combine(dataPath, "0.json");

  static string ReadDbVersion(string dataPath) =>
      JsonSerializer.Deserialize<ZoneTreeMeta>(File.ReadAllText(MetaFilePath(dataPath))).DbVersion;

  static void WriteDbVersion(string dataPath, string dbVersion)
  {
    var meta = JsonNode.Parse(File.ReadAllText(MetaFilePath(dataPath))).AsObject();
    if (dbVersion == null)
      meta.Remove(nameof(ZoneTreeMeta.DbVersion));
    else
      meta[nameof(ZoneTreeMeta.DbVersion)] = dbVersion;
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
  public void NewTreeHasTheCurrentDbVersion()
  {
    var dataPath = CreateDataPath();
    try
    {
      CreateTree(dataPath);
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
      using (var tree = CreateFactory(dataPath).OpenOrCreate())
        AssertRecords(tree);
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public void TreeMigratedBy2_1_10MovesToTheCurrentDbVersion()
  {
    var dataPath = CreateDataPath();
    try
    {
      CreateTree(dataPath);
      WriteDbVersion(dataPath, ZoneTreeInfo.MinimumDbVersion.ToString());

      using (var tree = CreateFactory(dataPath).OpenOrCreate())
      {
        Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
        AssertRecords(tree);
      }
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(CurrentDbVersion));
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [TestCase(null)]
  [TestCase("1.0.0")]
  [TestCase("2.1.9")]
  public void TreeThat2_1_10DidNotMigrateIsRefused(string dbVersion)
  {
    var dataPath = CreateDataPath();
    try
    {
      CreateTree(dataPath);
      WriteDbVersion(dataPath, dbVersion);

      var exception = Assert.Throws<ExistingDatabaseVersionIsNotCompatibleException>(
          () => CreateFactory(dataPath).OpenOrCreate().Dispose());
      Assert.That(exception.Message, Does.Contain("EnableZstdMigration()"));
      Assert.That(ReadDbVersion(dataPath), Is.EqualTo(dbVersion));
    }
    finally
    {
      DeleteDataPath(dataPath);
    }
  }

  [Test]
  public async Task RestoredTreeHasTheCurrentDbVersion()
  {
    var dataPath = CreateDataPath();
    var backupPath = dataPath + ".Backup";
    var restorePath = dataPath + ".Restore";
    try
    {
      CreateTree(dataPath);
      using (var tree = CreateFactory(dataPath).OpenOrCreate())
      {
        using var backup = tree.CreateLiveBackup(backupPath);
        backup.Start();
        backup.CreateGeneration();
        backup.Stop();
      }

      using (var restored = await CreateFactory(restorePath).RestoreFromLatestLiveBackup(backupPath))
        AssertRecords(restored);
      Assert.That(ReadDbVersion(restorePath), Is.EqualTo(CurrentDbVersion));
    }
    finally
    {
      DeleteDataPath(dataPath);
      DeleteDataPath(backupPath);
      DeleteDataPath(restorePath);
    }
  }

  [TestCase(null)]
  [TestCase("2.1.9")]
  public async Task BackupThat2_1_10DidNotTakeIsRefused(string dbVersion)
  {
    var provider = new ObjectStorageLiveBackupProvider(new ObjectStorageLiveBackupTests.MemoryStorage());
    var id = await provider.GetNextGenerationIdAsync(default);
    await provider.BeginGenerationAsync(id, DateTime.UtcNow, LiveBackupTreeKind.Plain, default);
    await provider.CompleteGenerationAsync(id, 0, dbVersion, default);

    var fileStreamProvider = new InMemoryFileStreamProvider();
    var exception = Assert.ThrowsAsync<LiveBackupVersionIsNotCompatibleException>(async () =>
        (await new ZoneTreeFactory<int, int>(fileStreamProvider)
            .SetDataDirectory("restored")
            .RestoreFromLatestLiveBackup(provider)).Dispose());
    Assert.That(exception.Message, Does.Contain("EnableZstdMigration()"));
  }
}
