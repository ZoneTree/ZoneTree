using System.Collections.Concurrent;
using ZoneTree.Options;
using ZoneTree.PresetTypes;
using ZoneTree.Serializers;

namespace ZoneTree.UnitTests;

public sealed class OpIndexTests
{
  [Test]
  public void TestOpIndex()
  {
    var dataPath = "data/TestOpIndex";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    var recordCount = 10_000;
    var opIndexes = new ConcurrentBag<long>();
    void CreateData()
    {
      using var zoneTree = new ZoneTreeFactory<int, int>()
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .SetDataDirectory(dataPath)
          .SetMutableSegmentMaxItemCount(100)
          .OpenOrCreate();

      using var maintainer = zoneTree.CreateMaintainer();
      Parallel.For(0, recordCount, (i) =>
      {
        var opIndex = zoneTree.Upsert(i, i);
        opIndexes.Add(opIndex);
      });
      maintainer.EvictToDisk();
      maintainer.WaitForBackgroundThreads();
    }

    void ReloadData(int expectedIndex, bool drop = false)
    {
      using var zoneTree = new ZoneTreeFactory<int, int>()
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .SetDataDirectory(dataPath)
          .SetMutableSegmentMaxItemCount(100)
          .Open();

      var opIndex = zoneTree.Upsert(expectedIndex, expectedIndex);
      Assert.That(opIndex, Is.EqualTo(expectedIndex));
      if (drop)
        zoneTree.Maintenance.Drop();
    }

    CreateData();
    ReloadData(recordCount + 1);
    ReloadData(recordCount + 2);
    ReloadData(recordCount + 3, true);

    Assert.IsTrue(
        opIndexes.Order().ToArray()
        .SequenceEqual(Enumerable.Range(1, recordCount).Select(x => (long)x)));
  }

  [Test]
  public void TestOpIndex2()
  {
    var dataPath = "data/TestOpIndex2";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    var recordCount = 10_000;
    var opIndexes = new ConcurrentBag<long>();
    void CreateData()
    {
      using var zoneTree = new ZoneTreeFactory<int, int>()
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .SetDataDirectory(dataPath)
          .SetMutableSegmentMaxItemCount(100)
          .OpenOrCreate();

      using var maintainer = zoneTree.CreateMaintainer();
      Parallel.For(0, recordCount, (i) =>
      {
        var opIndex = zoneTree.Upsert(i, i);
        opIndexes.Add(opIndex);
      });
      maintainer.EvictToDisk();
      maintainer.WaitForBackgroundThreads();
    }

    void ReloadData(int expectedIndex, bool drop = false)
    {
      using var zoneTree = new ZoneTreeFactory<int, int>()
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .SetDataDirectory(dataPath)
          .SetMutableSegmentMaxItemCount(100)
          .Open();

      var opIndex = zoneTree.Upsert(expectedIndex, expectedIndex);
      Assert.That(opIndex, Is.EqualTo(expectedIndex));

      using var maintainer = zoneTree.CreateMaintainer();
      maintainer.EvictToDisk();
      maintainer.WaitForBackgroundThreads();

      if (drop)
        zoneTree.Maintenance.Drop();
    }

    CreateData();
    ReloadData(recordCount + 1);
    ReloadData(recordCount + 2);
    ReloadData(recordCount + 3, true);

    Assert.IsTrue(
        opIndexes.Order().ToArray()
        .SequenceEqual(Enumerable.Range(1, recordCount).Select(x => (long)x)));
  }

  [Test]
  public void MaximumOpIndexCheckpointIsLoadedWhenMetaWalIsEmpty()
  {
    var dataPath = "data/MaximumOpIndexCheckpointIsLoadedWhenMetaWalIsEmpty";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    const int recordCount = 64;

    using (var zoneTree = new ZoneTreeFactory<int, int>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDataDirectory(dataPath)
        .SetMutableSegmentMaxItemCount(8)
        .OpenOrCreate())
    {
      for (var i = 0; i < recordCount; ++i)
      {
        Assert.That(zoneTree.Upsert(i, i), Is.EqualTo(i + 1));
      }

      using var maintainer = zoneTree.CreateMaintainer();
      maintainer.EvictToDisk();
      maintainer.WaitForBackgroundThreads();
      zoneTree.Maintenance.SaveMetadata();
    }

    using (var zoneTree = new ZoneTreeFactory<int, int>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDataDirectory(dataPath)
        .SetMutableSegmentMaxItemCount(8)
        .Open())
    {
      Assert.That(zoneTree.Upsert(recordCount, recordCount), Is.EqualTo(recordCount + 1));
      zoneTree.Maintenance.Drop();
    }
  }

  [Test]
  public void MaximumOpIndexIsLoadedFromMetaWalWhenMetadataCheckpointIsStale()
  {
    var dataPath = "data/MaximumOpIndexIsLoadedFromMetaWalWhenMetadataCheckpointIsStale";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    const int checkpointRecordCount = 8;
    const int recordCount = 16;

    using (var zoneTree = new ZoneTreeFactory<int, int>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDataDirectory(dataPath)
        .SetMutableSegmentMaxItemCount(64)
        .OpenOrCreate())
    {
      for (var i = 0; i < checkpointRecordCount; ++i)
      {
        Assert.That(zoneTree.Upsert(i, i), Is.EqualTo(i + 1));
      }
      zoneTree.Maintenance.SaveMetadata();

      for (var i = checkpointRecordCount; i < recordCount; ++i)
      {
        Assert.That(zoneTree.Upsert(i, i), Is.EqualTo(i + 1));
      }

      using var maintainer = zoneTree.CreateMaintainer();
      maintainer.EvictToDisk();
      maintainer.WaitForBackgroundThreads();
    }

    using (var zoneTree = new ZoneTreeFactory<int, int>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDataDirectory(dataPath)
        .SetMutableSegmentMaxItemCount(64)
        .Open())
    {
      Assert.That(zoneTree.Upsert(recordCount, recordCount), Is.EqualTo(recordCount + 1));
      zoneTree.Maintenance.Drop();
    }
  }

  [Test]
  public void SingleSegmentGarbageCollectionDoesNotResetNextOpIndex()
  {
    var dataPath = "data/SingleSegmentGarbageCollectionDoesNotResetNextOpIndex";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    const int key = 1;
    const int recordCount = 16;

    using (var zoneTree = new ZoneTreeFactory<int, int>()
        .Configure(options => options.WriteAheadLogOptions.Mode = WriteAheadLogMode.Private)
        .SetDataDirectory(dataPath)
        .OpenOrCreate())
    {
      for (var i = 0; i < recordCount; ++i)
      {
        Assert.That(zoneTree.Upsert(key, i), Is.EqualTo(i + 1));
      }
    }

    using (new ZoneTreeFactory<int, int>()
        .Configure(options =>
        {
          options.EnableSingleSegmentGarbageCollection = true;
          options.WriteAheadLogOptions.Mode = WriteAheadLogMode.Private;
        })
        .SetDataDirectory(dataPath)
        .Open())
    {
    }

    using (var zoneTree = new ZoneTreeFactory<int, int>()
        .Configure(options => options.WriteAheadLogOptions.Mode = WriteAheadLogMode.Private)
        .SetDataDirectory(dataPath)
        .Open())
    {
      Assert.That(zoneTree.Upsert(key, recordCount), Is.GreaterThan(recordCount));
      zoneTree.Maintenance.Drop();
    }
  }
}
