using System;
using ZoneTree.Collections;
using ZoneTree.Comparers;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.UnitTests;

public sealed class IteratorTests
{
  [Test]
  public void IntIntIterator()
  {
    var dataPath = "data/IntIntIterator";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(11)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetIsDeletedDelegate((in int key, in int value) => value == -1)
        .SetMarkValueDeletedDelegate((ref int value) => value = -1)
        .OpenOrCreate();
    var a = 250;
    var b = 500;
    for (var i = 0; i < a; ++i)
    {
      zoneTree.Upsert(i, i + i);
    }

    zoneTree.Maintenance.StartMergeOperation().Join();

    zoneTree.ForceDelete(127);
    zoneTree.ForceDelete(19);
    zoneTree.ForceDelete(20);
    zoneTree.ForceDelete(21);

    for (var i = a; i < b; ++i)
    {
      zoneTree.Upsert(i, i + i);
    }

    using var iterator = zoneTree.CreateIterator(IteratorType.AutoRefresh);

    for (var i = 0; i < b; ++i)
    {
      if (i == 19 || i == 20 || i == 21 || i == 127)
        continue;
      iterator.Next();
      Assert.That(iterator.CurrentKey, Is.EqualTo(i));
      Assert.That(iterator.CurrentValue, Is.EqualTo(i + i));
    }

    Assert.That(iterator.Next(), Is.False);
    Assert.That(zoneTree.Count(), Is.EqualTo(b - 4));

    using var reverseIterator = zoneTree.CreateReverseIterator(IteratorType.AutoRefresh);

    for (var i = b - 1; i >= 0; --i)
    {
      if (i == 19 || i == 20 || i == 21 || i == 127)
        continue;
      reverseIterator.Next();
      Assert.That(reverseIterator.CurrentKey, Is.EqualTo(i));
      Assert.That(reverseIterator.CurrentValue, Is.EqualTo(i + i));
    }

    Assert.That(reverseIterator.Next(), Is.False);

    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation().Join();

    Assert.That(zoneTree.Maintenance.DiskSegment.Length, Is.EqualTo(b - 4));
    zoneTree.Maintenance.SaveMetadata();
    iterator.Dispose();
    reverseIterator.Dispose();
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void IntIntIteratorSeek()
  {
    var dataPath = "data/IntIntIteratorSeek";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(11)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetIsDeletedDelegate((in int key, in int value) => value == -1)
        .SetMarkValueDeletedDelegate((ref int value) => value = -1)
        .OpenOrCreate();
    var a = 250;
    var b = 500;
    for (var i = 0; i < a; ++i)
    {
      zoneTree.Upsert(i, i + i);
    }

    zoneTree.Maintenance.StartMergeOperation().Join();

    zoneTree.ForceDelete(127);
    zoneTree.ForceDelete(19);
    zoneTree.ForceDelete(20);
    zoneTree.ForceDelete(21);

    for (var i = a; i < b; ++i)
    {
      zoneTree.Upsert(i, i + i);
    }

    using var iterator = zoneTree.CreateIterator(IteratorType.AutoRefresh);
    iterator.Seek(13);
    for (var i = 13; i < b; ++i)
    {
      if (i == 19 || i == 20 || i == 21 || i == 127)
        continue;
      iterator.Next();
      Assert.That(iterator.CurrentKey, Is.EqualTo(i));
      Assert.That(iterator.CurrentValue, Is.EqualTo(i + i));
    }

    Assert.That(iterator.Next(), Is.False);

    iterator.Seek(20);
    for (var i = 22; i < b; ++i)
    {
      if (i == 19 || i == 20 || i == 21 || i == 127)
        continue;
      iterator.Next();
      Assert.That(iterator.CurrentKey, Is.EqualTo(i));
      Assert.That(iterator.CurrentValue, Is.EqualTo(i + i));
    }

    Assert.That(iterator.Next(), Is.False);
    Assert.That(zoneTree.Count(), Is.EqualTo(b - 4));
    iterator.SeekFirst();
    Assert.That(iterator.Next(), Is.True);
    Assert.That(iterator.Next(), Is.True);
    Assert.That(iterator.CurrentKey, Is.EqualTo(1));

    using var reverseIterator = zoneTree.CreateReverseIterator(IteratorType.AutoRefresh);
    reverseIterator.Seek(451);
    for (var i = 451; i >= 0; --i)
    {
      if (i == 19 || i == 20 || i == 21 || i == 127)
        continue;
      reverseIterator.Next();
      Assert.That(reverseIterator.CurrentKey, Is.EqualTo(i));
      Assert.That(reverseIterator.CurrentValue, Is.EqualTo(i + i));
    }

    Assert.That(reverseIterator.Next(), Is.False);
    reverseIterator.SeekFirst();
    Assert.That(reverseIterator.Next(), Is.True);
    Assert.That(reverseIterator.Next(), Is.True);
    Assert.That(reverseIterator.CurrentKey, Is.EqualTo(b - 2));
    Assert.That(zoneTree.Count(), Is.EqualTo(b - 4));

    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation().Join();

    Assert.That(zoneTree.Maintenance.DiskSegment.Length, Is.EqualTo(b - 4));
    zoneTree.Maintenance.SaveMetadata();
    iterator.Dispose();
    reverseIterator.Dispose();
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void IntIntIteratorReflectNewInserts()
  {
    var dataPath = "data/IntIntIteratorReflectNewInserts";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(250)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetIsDeletedDelegate((in int key, in int value) => value == -1)
        .SetMarkValueDeletedDelegate((ref int value) => value = -1)
        .OpenOrCreate();
    var a = 251;
    var b = 500;
    for (var i = 1; i < a; i += 2)
    {
      zoneTree.Upsert(i, i + i);
    }

    zoneTree.Maintenance.StartMergeOperation().Join();

    for (var i = a; i < b; i += 2)
    {
      zoneTree.Upsert(i, i + i);
    }
    zoneTree.ForceDelete(11);
    zoneTree.ForceDelete(13);
    zoneTree.ForceDelete(15);
    using var iterator = zoneTree.CreateIterator(IteratorType.AutoRefresh);
    iterator.Seek(13);
    zoneTree.Upsert(24, 48);
    for (var i = 17; i < b; ++i)
    {
      if (i != 24 && i % 2 == 0)
        ++i;
      /*
       * New BPlusTree works with forward reading method.
       * This means inserts in the iterator position
       * of BPlusTree Leaf node does not reflect inserts.
       * This is not a bug. Callers can always double check
       * with TryGetKey() if they want to read most recent values
       * for every key they read from iteration.
       * Auto refresh property was made for MutableSegmentMoveForward
       * event. A manual refresh also works but it is expensive to call
       * for every key.
       */
      if (i == 23)
        iterator.Refresh();

      iterator.Next();
      Assert.That(iterator.CurrentKey, Is.EqualTo(i));
      Assert.That(iterator.CurrentValue, Is.EqualTo(i + i));
    }

    Assert.That(iterator.Next(), Is.False);
    Assert.That(iterator.Next(), Is.False);
    Assert.That(zoneTree.Count(), Is.EqualTo(b / 2 - 2));
    zoneTree.Maintenance.SaveMetadata();
    iterator.Dispose();
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void IntIntIteratorParallelInserts()
  {
    var dataPath = "data/IntIntIteratorParallelInserts";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    var random = Random.Shared;
    var insertCount = 100000;
    var iteratorCount = 1000;

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetMutableSegmentMaxItemCount(insertCount * 2)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    var task = Task.Factory.StartNew(() =>
    {
      Parallel.For(0, insertCount, (x) =>
          {
            var key = random.Next();
            zoneTree.Upsert(key, key + key);
          });
    });
    Parallel.For(0, iteratorCount, (x) =>
    {
      var initialCount = zoneTree.Maintenance.InMemoryRecordCount;
      using var iterator = zoneTree.CreateIterator(IteratorType.NoRefresh);
      iterator.SeekFirst();
      var counter = 0;
      var isValidData = true;
      while (iterator.Next())
      {
        var expected = iterator.CurrentKey + iterator.CurrentKey;
        if (iterator.CurrentValue != expected)
          isValidData = false;
        ++counter;
      }
      Assert.That(counter, Is.GreaterThanOrEqualTo(initialCount));
      Assert.That(isValidData, Is.True);
    });

    task.Wait();
    zoneTree.Maintenance.Drop();
  }

  [TestCase(true)]
  [TestCase(false)]
  public void IntIntReverseIteratorParallelInserts(bool reverse)
  {
    var dataPath = "data/IntIntReverseIteratorParallelInserts";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    var random = Random.Shared;
    var insertCount = 1000000;
    var iteratorCount = 1000;

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetMutableSegmentMaxItemCount(insertCount * 2)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .Configure(x =>
        {
          x.BPlusTreeLockMode = BPlusTreeLockMode.NodeLevelMonitor;
        })
        .OpenOrCreate();

    var task = Task.Run(() =>
    {
      Parallel.For(0, insertCount, (x) =>
          {
            var key = random.Next(0, 100_000);
            zoneTree.Upsert(key, key + key);
          });
    });
    Parallel.For(0, iteratorCount, (x) =>
    {
      var initialCount = zoneTree.Maintenance.MutableSegmentRecordCount;
      using var iterator =
              reverse ?
              zoneTree.CreateReverseIterator(IteratorType.NoRefresh) :
              zoneTree.CreateIterator(IteratorType.NoRefresh);
      iterator.SeekFirst();
      var counter = 0;
      var isValidData = true;
      var previousKey = reverse ? int.MaxValue : int.MinValue;
      while (iterator.Next())
      {
        var expected = iterator.CurrentKey + iterator.CurrentKey;
        if (iterator.CurrentValue != expected)
          isValidData = false;
        if (reverse && iterator.CurrentKey >= previousKey)
          throw new Exception("Reverse Iterator is not iterating in valid order.");

        if (!reverse && iterator.CurrentKey <= previousKey)
          throw new Exception("Iterator is not iterating in valid order.");

        previousKey = iterator.CurrentKey;
        ++counter;
      }
      Assert.That(counter, Is.GreaterThanOrEqualTo(initialCount));
      Assert.That(isValidData, Is.True);
    });

    task.Wait();
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void IntIntSnapshotIteratorParallelInserts()
  {
    var dataPath = "data/IntIntSnapshotIteratorParallelInserts";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    var random = Random.Shared;
    var insertCount = 100000;
    var iteratorCount = 1000;

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetMutableSegmentMaxItemCount(insertCount * 2)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    var task = Task.Factory.StartNew(() =>
    {
      Parallel.For(0, insertCount, (x) =>
          {
            zoneTree.Upsert(x, x + x);
          });
    });
    Parallel.For(0, iteratorCount, (x) =>
    {
      var initialCount = zoneTree.Maintenance.InMemoryRecordCount;
      using var iterator = zoneTree.CreateIterator(IteratorType.Snapshot);
      iterator.SeekFirst();
      var counter = 0;
      var isValidData = true;
      while (iterator.Next())
      {
        var expected = iterator.CurrentKey + iterator.CurrentKey;
        if (iterator.CurrentValue != expected)
          isValidData = false;
        ++counter;
      }
      Assert.That(counter, Is.GreaterThanOrEqualTo(initialCount));
      Assert.That(isValidData, Is.True);
    });

    task.Wait();
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void ReversePrefixSearch()
  {
    var dataPath = "data/ReversePrefixSearch";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<string, int>()
        .DisableDeletion()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();
    var a = 250;
    var b = 500;
    for (var i = a; i <= b; ++i)
    {
      var prefix = (i / 10).ToString() + "-";
      var key = prefix + i;
      zoneTree.Upsert(key, i);
    }

    using var reverseIterator = zoneTree.CreateReverseIterator();

    reverseIterator.Seek("41-");

    for (var i = 409; i >= 250; --i)
    {
      var prefix = (i / 10).ToString() + "-";
      var key = prefix + i;
      Assert.That(reverseIterator.Next(), Is.True);
      Assert.That(reverseIterator.CurrentKey, Is.EqualTo(key));
      Assert.That(reverseIterator.CurrentValue, Is.EqualTo(i));
    }

    Assert.That(reverseIterator.CurrentKey, Is.EqualTo("25-250"));
    Assert.That(reverseIterator.CurrentValue, Is.EqualTo(250));

    Assert.That(reverseIterator.Next(), Is.False);
    Assert.That(zoneTree.Count(), Is.EqualTo(b - a + 1));
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void PrefixSearch()
  {
    var dataPath = "data/PrefixSearch";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<string, int>()
        .DisableDeletion()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();
    var a = 250;
    var b = 500;
    for (var i = a; i <= b; ++i)
    {
      var prefix = (i / 10).ToString() + "-";
      var key = prefix + i;
      zoneTree.Upsert(key, i);
    }

    using var iterator = zoneTree.CreateIterator();

    iterator.Seek("41-");

    for (var i = 410; i <= 500; ++i)
    {
      var prefix = (i / 10).ToString() + "-";
      var key = prefix + i;
      Assert.That(iterator.Next(), Is.True);
      Assert.That(iterator.CurrentKey, Is.EqualTo(key));
      Assert.That(iterator.CurrentValue, Is.EqualTo(i));
    }

    Assert.That(iterator.CurrentKey, Is.EqualTo("50-500"));
    Assert.That(iterator.CurrentValue, Is.EqualTo(500));

    Assert.That(iterator.Next(), Is.False);
    Assert.That(zoneTree.Count(), Is.EqualTo(b - a + 1));
    zoneTree.Maintenance.Drop();
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void PrefetchingDiskIteratorScansFixedSizeKeyAndValueSegment(DiskSegmentMode diskSegmentMode)
  {
    AssertPrefetchingDiskIteratorScans(
        "FixedSizeKeyAndValue",
        diskSegmentMode,
        i => i,
        i => i * 10,
        factory => factory.DisableDeletion(),
        Comparer<int>.Default);
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void PrefetchingDiskIteratorScansFixedSizeKeySegment(DiskSegmentMode diskSegmentMode)
  {
    AssertPrefetchingDiskIteratorScans(
        "FixedSizeKey",
        diskSegmentMode,
        i => i,
        i => "value-" + i.ToString("D4"),
        factory => factory
            .DisableDeletion()
            .SetValueSerializer(new Utf8StringSerializer()),
        Comparer<int>.Default);
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void PrefetchingDiskIteratorScansFixedSizeValueSegment(DiskSegmentMode diskSegmentMode)
  {
    AssertPrefetchingDiskIteratorScans(
        "FixedSizeValue",
        diskSegmentMode,
        i => "key-" + i.ToString("D4"),
        i => i * 10,
        factory => factory
            .DisableDeletion()
            .SetComparer(new StringOrdinalComparerAscending())
            .SetKeySerializer(new Utf8StringSerializer()),
        StringComparer.Ordinal);
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void PrefetchingDiskIteratorScansVariableSizeSegment(DiskSegmentMode diskSegmentMode)
  {
    AssertPrefetchingDiskIteratorScans(
        "VariableSize",
        diskSegmentMode,
        i => "key-" + i.ToString("D4"),
        i => "value-" + i.ToString("D4"),
        factory => factory
            .DisableDeletion()
            .SetComparer(new StringOrdinalComparerAscending())
            .SetKeySerializer(new Utf8StringSerializer())
            .SetValueSerializer(new Utf8StringSerializer()),
        StringComparer.Ordinal);
  }

  [TestCase(true, DiskSegmentMode.SingleDiskSegment, 0, 0)]
  [TestCase(false, DiskSegmentMode.SingleDiskSegment, 0, 0)]
  [TestCase(true, DiskSegmentMode.MultiPartDiskSegment, 0, 0)]
  [TestCase(true, DiskSegmentMode.MultiPartDiskSegment, 3, 7)]
  public void SeekIteratorsAfterMerge(
      bool merge,
      DiskSegmentMode diskSegmentMode,
      int minimumRecordCount,
      int maximumRecordCount)
  {
    var dataPath = "data/SeekIteratorsAfterMerge" + merge + diskSegmentMode + minimumRecordCount + maximumRecordCount;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<string, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetComparer(new StringCurrentCultureComparerAscending())
        .SetKeySerializer(new Utf8StringSerializer())
        .SetValueSerializer(new Int32Serializer())
        .ConfigureDiskSegmentOptions(x =>
        {
          x.Mode = diskSegmentMode;
          if (minimumRecordCount > 0)
            x.MinimumRecordCount = minimumRecordCount;
          if (maximumRecordCount > 0)
            x.MaximumRecordCount = maximumRecordCount;
        })
        .OpenOrCreate();
    var list = PrepareData1(zoneTree, 500);

    zoneTree.Maintenance.MoveMutableSegmentForward();
    if (merge)
      zoneTree.Maintenance.StartMergeOperation()?.Join();

    var random = Random.Shared;
    DoPrefixSearch(zoneTree, list, random);
    zoneTree.Maintenance.DiskSegment.InitSparseArray(100);
    DoPrefixSearch(zoneTree, list, random);
    zoneTree.Maintenance.Drop();
  }

  static List<Tuple<string, int>> PrepareData1(IZoneTree<string, int> zoneTree, int n)
  {
    var list = new List<Tuple<string, int>>();
    for (var i = 0; i < n; ++i)
    {
      var key = $"myprefix1|string{i}";
      zoneTree.Upsert(key, i);
      list.Add(Tuple.Create(key, i));

    }
    for (var i = 0; i < n; ++i)
    {
      var key = $"myprefix2|string{i}";
      zoneTree.Upsert(key, i);
      list.Add(Tuple.Create(key, i));
    }
    for (var i = 0; i < n; ++i)
    {
      var key = $"myprefix3|string{i}";
      zoneTree.Upsert(key, i);
      list.Add(Tuple.Create(key, i));
    }
    for (var i = 0; i < n; ++i)
    {
      var key = $"myprefix4|string{i}";
      zoneTree.Upsert(key, i);
      list.Add(Tuple.Create(key, i));
    }
    list.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.CurrentCulture));
    return list;
  }

  static void AssertPrefetchingDiskIteratorScans<TKey, TValue>(
      string name,
      DiskSegmentMode diskSegmentMode,
      Func<int, TKey> keyFactory,
      Func<int, TValue> valueFactory,
      Func<ZoneTreeFactory<TKey, TValue>, ZoneTreeFactory<TKey, TValue>> configureFactory,
      IComparer<TKey> expectedComparer)
  {
    var dataPath = "data/PrefetchingDiskIteratorScans" + name + diskSegmentMode;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    const int recordCount = 37;
    var expected = new List<KeyValuePair<TKey, TValue>>(recordCount);

    using var zoneTree = configureFactory(new ZoneTreeFactory<TKey, TValue>())
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(5)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureWriteAheadLogOptions(options =>
            options.Mode = WriteAheadLogMode.None)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.Mode = diskSegmentMode;
          if (diskSegmentMode == DiskSegmentMode.MultiPartDiskSegment)
          {
            options.MinimumRecordCount = 3;
            options.MaximumRecordCount = 5;
          }
        })
        .OpenOrCreate();

    for (var i = 0; i < recordCount; ++i)
    {
      var key = keyFactory(i);
      var value = valueFactory(i);
      expected.Add(KeyValuePair.Create(key, value));
      zoneTree.Upsert(key, value);
    }

    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation().Join();
    expected.Sort((x, y) => expectedComparer.Compare(x.Key, y.Key));

    var iteratorOptions = new IteratorOptions
    {
      IteratorType = IteratorType.NoRefresh,
      DiskSegmentPrefetchSize = 3
    };

    using (var iterator = zoneTree.CreateIterator(iteratorOptions))
    {
      for (var i = 0; i < expected.Count; ++i)
      {
        Assert.That(iterator.Next(), Is.True);
        Assert.That(iterator.CurrentKey, Is.EqualTo(expected[i].Key));
        Assert.That(iterator.CurrentValue, Is.EqualTo(expected[i].Value));
      }
      Assert.That(iterator.Next(), Is.False);
    }

    using (var iterator = zoneTree.CreateIterator(iteratorOptions))
    {
      iterator.Seek(expected[10].Key);
      for (var i = 10; i < expected.Count; ++i)
      {
        Assert.That(iterator.Next(), Is.True);
        Assert.That(iterator.CurrentKey, Is.EqualTo(expected[i].Key));
        Assert.That(iterator.CurrentValue, Is.EqualTo(expected[i].Value));
      }
      Assert.That(iterator.Next(), Is.False);
    }

    using (var reverseIterator = zoneTree.CreateReverseIterator(iteratorOptions))
    {
      for (var i = expected.Count - 1; i >= 0; --i)
      {
        Assert.That(reverseIterator.Next(), Is.True);
        Assert.That(reverseIterator.CurrentKey, Is.EqualTo(expected[i].Key));
        Assert.That(reverseIterator.CurrentValue, Is.EqualTo(expected[i].Value));
      }
      Assert.That(reverseIterator.Next(), Is.False);
    }

    zoneTree.Maintenance.Drop();
  }

  [TestCase(true, DiskSegmentMode.SingleDiskSegment, 0, 0)]
  [TestCase(false, DiskSegmentMode.SingleDiskSegment, 0, 0)]
  [TestCase(true, DiskSegmentMode.MultiPartDiskSegment, 0, 0)]
  [TestCase(true, DiskSegmentMode.MultiPartDiskSegment, 3, 7)]
  public void SeekIteratorsAfterMergeReload(
      bool merge,
      DiskSegmentMode diskSegmentMode,
      int minimumRecordCount,
      int maximumRecordCount)
  {
    var dataPath = "data/SeekIteratorsAfterMergeReload" + merge + diskSegmentMode + minimumRecordCount + maximumRecordCount;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    var zoneTree = new ZoneTreeFactory<string, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetComparer(new StringCurrentCultureComparerAscending())
        .SetKeySerializer(new Utf8StringSerializer())
        .SetValueSerializer(new Int32Serializer())
        .ConfigureDiskSegmentOptions(x =>
        {
          x.Mode = diskSegmentMode;
          if (minimumRecordCount > 0)
            x.MinimumRecordCount = minimumRecordCount;
          if (maximumRecordCount > 0)
            x.MaximumRecordCount = maximumRecordCount;
        })
        .OpenOrCreate();

    var list = PrepareData1(zoneTree, 500);
    zoneTree.Maintenance.MoveMutableSegmentForward();
    if (merge)
      zoneTree.Maintenance.StartMergeOperation()?.Join();

    zoneTree.Dispose();
    zoneTree = new ZoneTreeFactory<string, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetComparer(new StringCurrentCultureComparerAscending())
        .SetKeySerializer(new Utf8StringSerializer())
        .SetValueSerializer(new Int32Serializer())
        .ConfigureDiskSegmentOptions(x =>
        {
          x.Mode = diskSegmentMode;
          if (minimumRecordCount > 0)
            x.MinimumRecordCount = minimumRecordCount;
          if (maximumRecordCount > 0)
            x.MaximumRecordCount = maximumRecordCount;
        })
        .Open();
    var random = Random.Shared;
    DoPrefixSearch(zoneTree, list, random);
    zoneTree.Maintenance.DiskSegment.InitSparseArray(100);
    DoPrefixSearch(zoneTree, list, random);
  }

  private static void DoPrefixSearch(IZoneTree<string, int> zoneTree, List<Tuple<string, int>> list, Random random)
  {
    for (var i = 0; i <= 5; ++i)
    {
      TestForward(zoneTree, list, "myprefix" + i);
      TestBackward(zoneTree, list, "myprefix" + i);
    }
    for (var i = 0; i < 100; ++i)
    {
      var prefix = list[random.Next(0, list.Count - 1)].Item1;
      TestForward(zoneTree, list, prefix);
      TestBackward(zoneTree, list, prefix);
    }
  }

  static void TestBackward(IZoneTree<string, int> zoneTree, List<Tuple<string, int>> list, string prefix)
  {
    using var revIterator = zoneTree.CreateReverseIterator();
    var listIndex = list.FindIndex(x => x.Item1.StartsWith(prefix));
    var comparer = new StringCurrentCultureComparerAscending();
    if (listIndex == -1)
    {
      listIndex = comparer.Compare(prefix, list[0].Item1) < 0 ? -1 : list.Count - 1;
    }
    else
    {
      if (comparer.Compare(prefix, list[listIndex].Item1) != 0)
        --listIndex;
    }
    revIterator.Seek(prefix);
    while (revIterator.Next())
    {
      var key = revIterator.CurrentKey;
      var value = revIterator.CurrentValue;
      (var expectedKey, var expectedValue) = list[listIndex--];
      Assert.That(key, Is.EqualTo(expectedKey));
      Assert.That(value, Is.EqualTo(expectedValue));
    }
    Assert.That(listIndex, Is.EqualTo(-1));
  }

  static void TestForward(IZoneTree<string, int> zoneTree, List<Tuple<string, int>> list, string prefix)
  {
    using var iterator = zoneTree.CreateIterator();

    var listIndex = list.FindIndex(x => x.Item1.StartsWith(prefix));
    if (listIndex < 0)
    {
      var comparer = new StringCurrentCultureComparerAscending();
      listIndex = comparer.Compare(prefix, list[0].Item1) < 0 ? 0 : list.Count;
    }
    iterator.Seek(prefix);
    while (iterator.Next())
    {
      var key = iterator.CurrentKey;
      var value = iterator.CurrentValue;
      (var expectedKey, var expectedValue) = list[listIndex++];
      Assert.That(key, Is.EqualTo(expectedKey));
      Assert.That(value, Is.EqualTo(expectedValue));
    }
    Assert.That(listIndex, Is.EqualTo(list.Count));
  }
}
