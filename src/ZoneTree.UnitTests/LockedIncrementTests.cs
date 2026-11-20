using ZoneTree.Comparers;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class LockedIncrementTests
{
  [TestCase(WriteAheadLogMode.AsyncPrivate)]
  [TestCase(WriteAheadLogMode.Private)]
  public void IntIntLockedIncrement(WriteAheadLogMode walMode)
  {
    var dataPath = "data/IntIntLockedIncrement." + walMode;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    var counterKey = -3999;
    using var data = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetComparer(new Int32ComparerDescending())
        .SetMutableSegmentMaxItemCount(500)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureWriteAheadLogOptions(x => x.Mode = walMode)
        .OpenOrCreate();
    for (var i = 0; i < 2000; ++i)
    {
      data.Upsert(i, i + i);
    }
    data.Maintenance.MoveMutableSegmentForward();
    data.Maintenance.StartMergeOperation().Join();
    var random = Random.Shared;
    var off = 0;
    Parallel.For(0, 1001, (x) =>
    {
      var len = random.Next(1501);
      for (var i = 0; i < len; ++i)
      {
        data.Upsert(i, i + i);
      }
      len = random.Next(1501);
      for (var i = 0; i < len; ++i)
      {
        using (data.Lock(counterKey))
        {
          data.TryGet(counterKey, out var y);
          data.Upsert(counterKey, y + 1);
        }
        Interlocked.Increment(ref off);
      }
    });

    for (var i = 0; i < 2000; ++i)
    {
      var result = data.TryGet(i, out var v);
      Assert.Multiple(() =>
      {
        Assert.That(result, Is.True);
        Assert.That(v, Is.EqualTo(i + i));
        Assert.That(data.ContainsKey(i), Is.True);
      });
    }

    data.Maintenance.MoveMutableSegmentForward();
    data.Maintenance.StartMergeOperation().Join();
    data.TryGet(counterKey, out var finalValue);
    Assert.That(finalValue, Is.EqualTo(off));
    data.Maintenance.Drop();
  }

  [TestCase(WriteAheadLogMode.AsyncPrivate)]
  [TestCase(WriteAheadLogMode.Private)]
  public void IntIntLockedIncrementForBPlusTree(WriteAheadLogMode walMode)
  {
    var dataPath = "data/IntIntLockedIncrementForBPlusTree." + walMode;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var data = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetComparer(new Int32ComparerDescending())
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureWriteAheadLogOptions(x => x.Mode = walMode)
        .OpenOrCreate();
    for (var i = 0; i < 2000; ++i)
    {
      data.Upsert(i, i + i);
    }
    var random = Random.Shared;
    var off = 0;
    Parallel.For(0, 1001, (x) =>
    {
      var len = random.Next(300);
      for (var i = 0; i < len; ++i)
      {
        data.Upsert(i, i + i);
      }
      len = random.Next(1501);
      for (var i = 0; i < len; ++i)
      {
        using (data.Lock(3999))
        {
          data.TryGet(3999, out var y);
          data.Upsert(3999, y + 1);
        }
        Interlocked.Increment(ref off);
      }

    });

    for (var i = 0; i < 2000; ++i)
    {
      var result = data.TryGet(i, out var v);
      Assert.Multiple(() =>
      {
        Assert.That(result, Is.True);
        Assert.That(v, Is.EqualTo(i + i));
        Assert.That(data.ContainsKey(i), Is.True);
      });
    }

    data.TryGet(3999, out var finalValue);
    Assert.That(finalValue, Is.EqualTo(off));
    data.Maintenance.Drop();
  }

  [TestCase(WriteAheadLogMode.AsyncPrivate)]
  [TestCase(WriteAheadLogMode.Private)]
  public void IntIntMutableSegmentOnlyLockedIncrement(WriteAheadLogMode walMode)
  {
    var dataPath = "data/IntIntMutableSegmentOnlyLockedIncrement." + walMode;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    var counterKey = -3999;
    using var data = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetComparer(new Int32ComparerDescending())
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureWriteAheadLogOptions(x => x.Mode = walMode)
        .OpenOrCreate();
    for (var i = 0; i < 2000; ++i)
    {
      data.Upsert(i, i + i);
    }
    var random = Random.Shared;
    var off = 0;
    Parallel.For(0, 1001, (x) =>
    {
      var len = random.Next(1501);
      for (var i = 0; i < len; ++i)
      {
        data.Upsert(i, i + i);
      }
      len = random.Next(1501);
      for (var i = 0; i < len; ++i)
      {
        using (data.Lock(counterKey))
        {
          data.TryGet(counterKey, out var y);
          data.Upsert(counterKey, y + 1);
        }
        Interlocked.Increment(ref off);
      }

    });

    for (var i = 0; i < 2000; ++i)
    {
      var result = data.TryGet(i, out var v);
      Assert.Multiple(() =>
      {
        Assert.That(result, Is.True);
        Assert.That(v, Is.EqualTo(i + i));
        Assert.That(data.ContainsKey(i), Is.True);
      });
    }

    data.TryGet(counterKey, out var finalValue);
    Assert.That(finalValue, Is.EqualTo(off));
    data.Maintenance.Drop();
  }

  [TestCase(WriteAheadLogMode.AsyncPrivate)]
  [TestCase(WriteAheadLogMode.Private)]
  public void IntIntMutableSegmentSeveralUpserts(WriteAheadLogMode walMode)
  {
    var dataPath = "data/IntIntMutableSegmentSeveralUpserts." + walMode;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    using var data = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetComparer(new Int32ComparerDescending())
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureWriteAheadLogOptions(x => x.Mode = walMode)
        .OpenOrCreate();
    var n = 1000;
    var random = Random.Shared;
    Parallel.For(0, 1000, (x) =>
    {
      var len = random.Next(n);
      for (var i = 0; i < len; ++i)
      {
        var k = random.Next();
        data.Upsert(k, k + k);
      }

    });
    using var iterator = data.CreateIterator(IteratorType.NoRefresh);
    while (iterator.Next())
    {
      var k = iterator.CurrentKey;
      var v = iterator.CurrentValue;
      Assert.Multiple(() =>
      {
        Assert.That(v, Is.EqualTo(k + k));
        Assert.That(data.ContainsKey(k), Is.True);
      });
    }

    data.Maintenance.Drop();
  }
}
