using ZoneTree.Comparers;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class AtomicUpdateTests
{
  [TestCase(WriteAheadLogMode.AsyncPrivate)]
  [TestCase(WriteAheadLogMode.Private)]
  public void IntIntAtomicIncrement(WriteAheadLogMode walMode)
  {
    var dataPath = "data/IntIntAtomicIncrement." + walMode;
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
        .ConfigureWriteAheadLogOptions(x => x.WriteAheadLogMode = walMode)
        .OpenOrCreate();
    for (var i = 0; i < 2000; ++i)
    {
      data.Upsert(i, i + i);
    }
    data.Maintenance.MoveMutableSegmentForward();
    data.Maintenance.StartMergeOperation().Join();
    var random = Random.Shared;
    var off = -1;
    Parallel.For(0, 1001, (x) =>
    {
      try
      {
        var len = random.Next(1501);
        for (var i = 0; i < len; ++i)
        {
          data.Upsert(i, i + i);
        }
        len = random.Next(1501);
        for (var i = 0; i < len; ++i)
        {
          data.TryAtomicAddOrUpdate(counterKey, 0,
                  bool (ref int y) =>
                  {
                    ++y;
                    return true;
                  },
                  (in int _, long _, OperationResult result) =>
                  {
                  }
              );
          Interlocked.Increment(ref off);
        }
      }
      catch (Exception e)
      {
        Console.WriteLine(e.ToString());
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
  public void IntIntAtomicIncrementForBTree(WriteAheadLogMode walMode)
  {
    var dataPath = "data/IntIntAtomicIncrementForBTree." + walMode;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var data = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetComparer(new Int32ComparerDescending())
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureWriteAheadLogOptions(x => x.WriteAheadLogMode = walMode)
        .OpenOrCreate();
    for (var i = 0; i < 2000; ++i)
    {
      data.Upsert(i, i + i);
    }
    var random = Random.Shared;
    var off = -1;
    Parallel.For(0, 1001, (x) =>
    {
      try
      {
        var len = random.Next(300);
        for (var i = 0; i < len; ++i)
        {
          data.Upsert(i, i + i);
        }
        len = random.Next(1501);
        for (var i = 0; i < len; ++i)
        {
          data.TryAtomicAddOrUpdate(3999, 0,
                  bool (ref int y) =>
                  {
                    ++y;
                    return true;
                  });
          Interlocked.Increment(ref off);
        }

      }
      catch (Exception e)
      {
        Console.WriteLine(e.ToString());
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
  public void IntIntMutableSegmentOnlyAtomicIncrement(WriteAheadLogMode walMode)
  {
    var dataPath = "data/IntIntMutableSegmentOnlyAtomicIncrement." + walMode;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    var counterKey = -3999;
    using var data = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetComparer(new Int32ComparerDescending())
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureWriteAheadLogOptions(x => x.WriteAheadLogMode = walMode)
        .OpenOrCreate();
    for (var i = 0; i < 2000; ++i)
    {
      data.Upsert(i, i + i);
    }
    var random = Random.Shared;
    var off = -1;
    Parallel.For(0, 1001, (x) =>
    {
      try
      {
        var len = random.Next(1501);
        for (var i = 0; i < len; ++i)
        {
          data.Upsert(i, i + i);
        }
        len = random.Next(1501);
        for (var i = 0; i < len; ++i)
        {
          data.TryAtomicAddOrUpdate(counterKey, 0,
                  bool (ref int y) =>
                  {
                    ++y;
                    return true;
                  });
          Interlocked.Increment(ref off);
        }

      }
      catch (Exception e)
      {
        Console.WriteLine(e.ToString());
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
        .ConfigureWriteAheadLogOptions(x => x.WriteAheadLogMode = walMode)
        .OpenOrCreate();
    var n = 1000;
    var random = Random.Shared;
    Parallel.For(0, 1000, (x) =>
    {
      try
      {
        var len = random.Next(n);
        for (var i = 0; i < len; ++i)
        {
          var k = random.Next();
          data.Upsert(k, k + k);
        }

      }
      catch (Exception e)
      {
        Console.WriteLine(e.ToString());
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

  [Test]
  public void TryAtomicAddOrUpdate_ResultDelegate_ReceivesAddedValue()
  {
    var dataPath = "data/TryAtomicAddOrUpdate_ResultDelegate_ReceivesAddedValue";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    using var tree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    int callbackValue = -1;
    long callbackOpIndex = -1;
    OperationResult callbackResult = default;

    var added = tree.TryAtomicAddOrUpdate(
        1,
        123,
        valueUpdater: (ref int value) =>
        {
          value++;
          return true;
        },
        result: (in int value, long opIndex, OperationResult result) =>
        {
          callbackValue = value;
          callbackOpIndex = opIndex;
          callbackResult = result;
        });

    Assert.That(added, Is.True);
    Assert.That(tree.TryGet(1, out var stored), Is.True);
    Assert.That(stored, Is.EqualTo(123));

    // This is the disputed assertion.
    Assert.That(callbackValue, Is.EqualTo(123));
    Assert.That(callbackResult, Is.EqualTo(OperationResult.Added));
    Assert.That(callbackOpIndex, Is.GreaterThan(0));
  }
}
