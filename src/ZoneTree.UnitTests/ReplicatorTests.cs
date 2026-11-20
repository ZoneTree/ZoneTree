using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Serializers;

namespace ZoneTree.UnitTests;

public sealed class ReplicatorTests
{
  [Test]
  public void TestReplicator()
  {
    var dataPath = "data/TestReplicator";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    var recordCount = 50_000;
    var keyCount = 15_000;
    var maxMemory = 2_000;
    void CreateData()
    {
      using var zoneTree = new ZoneTreeFactory<int, int>()
          .SetDataDirectory(dataPath + "/source")
          .SetMutableSegmentMaxItemCount(maxMemory)
          .OpenOrCreate();

      using var replica = new ZoneTreeFactory<int, int>()
          .SetDataDirectory(dataPath + "/replica")
          .SetMutableSegmentMaxItemCount(maxMemory)
          .OpenOrCreate();

      using var replicator = new Replicator<int, int>(replica, dataPath + "/replica-op-index");
      using var maintainer1 = zoneTree.CreateMaintainer();
      using var maintainer2 = replica.CreateMaintainer();
      int replicated = 0;
      var k = 0;
      Parallel.For(0, recordCount, (i) =>
      {
        var key = i % keyCount;
        var value = Interlocked.Increment(ref k);
        var opIndex = zoneTree.Upsert(key, value);
        Task.Run(() =>
              {
                replicator.OnUpsert(key, value, opIndex);
                Interlocked.Increment(ref replicated);
              });
      });
      while (replicated < recordCount) Task.Delay(500).Wait();
      maintainer1.EvictToDisk();
      maintainer2.EvictToDisk();
      maintainer1.WaitForBackgroundThreads();
      maintainer2.WaitForBackgroundThreads();
    }

    void TestEqual()
    {
      using var zoneTree = new ZoneTreeFactory<int, int>()
          .SetDataDirectory(dataPath + "/source")
          .Open();

      using var replica = new ZoneTreeFactory<int, int>()
          .SetDataDirectory(dataPath + "/replica")
          .Open();

      using var iterator1 = zoneTree.CreateIterator();
      using var iterator2 = replica.CreateIterator();
      var i = 0;
      while (true)
      {
        var n1 = iterator1.Next();
        var n2 = iterator2.Next();
        Assert.That(n2, Is.EqualTo(n1));
        if (!n1) break;
        Assert.That(iterator2.Current, Is.EqualTo(iterator1.Current));
        ++i;
      }
      Assert.That(i, Is.EqualTo(keyCount));
      zoneTree.Maintenance.Drop();
      replica.Maintenance.Drop();
    }

    CreateData();
    TestEqual();
  }

  [Test]
  public void TestReplicator2()
  {
    for (int i = 0; i < 5; i++)
    {
      var dataPath = "data/TestReplicator";
      if (Directory.Exists(dataPath))
        Directory.Delete(dataPath, true);

      var recordCount = 50_000;
      var keyCount = 15_000;
      var maxMemory = 2_000;

      void CreateData()
      {
        using var zoneTree = new ZoneTreeFactory<int, int>()
            .SetDataDirectory(dataPath + "/source")
            .SetMutableSegmentMaxItemCount(maxMemory)
            .OpenOrCreate();

        using var replica = new ZoneTreeFactory<int, int>()
            .SetDataDirectory(dataPath + "/replica")
            .SetMutableSegmentMaxItemCount(maxMemory)
            .OpenOrCreate();

        using var replicator = new Replicator<int, int>(replica, dataPath + "/replica-op-index");
        using var maintainer1 = zoneTree.CreateMaintainer();
        using var maintainer2 = replica.CreateMaintainer();
        int replicated = 0;
        var k = 0;
        Parallel.For(0, recordCount, (i) =>
        {
          var key = i % keyCount;
          var value = Interlocked.Increment(ref k);
          var opIndex = zoneTree.Upsert(key, value);
          Task.Run(() =>
                  {
                    replicator.OnUpsert(key, value, opIndex);
                    Interlocked.Increment(ref replicated);
                  });
        });
        while (replicated < recordCount) Task.Delay(500).Wait();
        maintainer1.EvictToDisk();
        maintainer2.EvictToDisk();
        maintainer1.WaitForBackgroundThreads();
        maintainer2.WaitForBackgroundThreads();
      }

      void TestEqual()
      {
        using var zoneTree = new ZoneTreeFactory<int, int>()
            .SetDataDirectory(dataPath + "/source")
            .Open();

        using var replica = new ZoneTreeFactory<int, int>()
            .SetDataDirectory(dataPath + "/replica")
            .Open();

        using var iterator1 = zoneTree.CreateIterator();
        using var iterator2 = replica.CreateIterator();
        var i = 0;
        while (true)
        {
          var n1 = iterator1.Next();
          var n2 = iterator2.Next();
          Assert.That(n2, Is.EqualTo(n1));
          if (!n1) break;
          Assert.That(iterator2.Current, Is.EqualTo(iterator1.Current));
          ++i;
        }
        Assert.That(i, Is.EqualTo(keyCount));
        zoneTree.Maintenance.Drop();
        replica.Maintenance.Drop();
      }
      CreateData();
      TestEqual();
    }
  }
  readonly record struct Point(int X);

  sealed class PointComparer : IRefComparer<Point>
  {
    public int Compare(in Point x, in Point y) => x.X.CompareTo(y.X);
  }

  sealed class PointSerializer : ISerializer<Point>
  {
    readonly Int32Serializer Inner = new();

    public Point Deserialize(Memory<byte> bytes) => new(Inner.Deserialize(bytes));

    public Memory<byte> Serialize(in Point entry) => Inner.Serialize(entry.X);

    public int GetMaxByteCount(in Point entry) => Inner.GetMaxByteCount(entry.X);

    public int Serialize(in Point entry, Span<byte> destination) => Inner.Serialize(entry.X, destination);
  }

  [Test]
  public void ReplicatorWorksWithoutAKeyHasher()
  {
    // A custom key type has no default key hasher, so the index locks every key with one lock.
    var dataPath = "data/ReplicatorWorksWithoutAKeyHasher";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    using var replica = new ZoneTreeFactory<Point, int>(new InMemoryFileStreamProvider())
        .SetDataDirectory("replica")
        .SetLogger(SilentTestLogger.Instance)
        .SetComparer(new PointComparer())
        .SetKeySerializer(new PointSerializer())
        .SetMutableSegmentBloomFilterBitsPerItem(0)
        .Configure(options => options.LockStripeCount = 1)
        .OpenOrCreate();
    using (var replicator = new Replicator<Point, int>(replica, dataPath, false, factory => factory
        .SetLogger(SilentTestLogger.Instance)
        .SetComparer(new PointComparer())
        .SetKeySerializer(new PointSerializer())
        .SetMutableSegmentBloomFilterBitsPerItem(0)))
    {
      replicator.OnUpsert(new Point(1), 10, 2);
      // An older operation index does not replace the newer value.
      replicator.OnUpsert(new Point(1), 5, 1);
    }
    Assert.That(replica.TryGet(new Point(1), out var value), Is.True);
    Assert.That(value, Is.EqualTo(10));
    Directory.Delete(dataPath, true);
  }
}
