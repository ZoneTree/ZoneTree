using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.UnitTests;

public sealed class FixedSizeKeyAndValueTests
{
  [Test]
  public void IntIntTreeTest()
  {
    var dataPath = "data/IntIntTreeTest";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    using var data = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(5)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    for (var i = 0; i < 2000; ++i)
    {
      data.Upsert(i, i + i);
    }
    data.Maintenance.MoveMutableSegmentForward();
    data.Maintenance.StartMergeOperation().Join();
    for (var i = 0; i < 2000; ++i)
    {
      var result = data.TryGet(i, out var v);
      Assert.That(result, Is.True);
      Assert.That(v, Is.EqualTo(i + i));
      Assert.That(data.ContainsKey(i), Is.True);
    }
    data.Maintenance.Drop();
  }

  [Test]
  public void FirstMultiPartMergeDoesNotTreatReadOnlySegmentAsDisk()
  {
    var dataPath = "data/FirstMultiPartMergeDoesNotTreatReadOnlySegmentAsDisk";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var data = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(100)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DiskSegmentMode = DiskSegmentMode.MultiPartDiskSegment;
          options.MinimumRecordCount = 2;
          options.MaximumRecordCount = 3;
        })
        .OpenOrCreate();

    for (var i = 0; i < 4; ++i)
    {
      data.Upsert(i, i);
    }

    data.Maintenance.MoveMutableSegmentForward();
    data.Maintenance.StartMergeOperation().Join();

    Assert.That(data.Maintenance.DiskSegment.GetPartCount(), Is.EqualTo(2));
    data.Maintenance.Drop();
  }

  [Test]
  public void IntStringTreeTest()
  {
    var dataPath = "data/IntStringTreeTest";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var data = new ZoneTreeFactory<int, string>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(5)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetValueSerializer(new UnicodeStringSerializer())
        .OpenOrCreate();
    int n = 2000;
    for (var i = 0; i < n; ++i)
    {
      data.Upsert(i, (i + i).ToString());
    }
    data.Maintenance.MoveMutableSegmentForward();
    data.Maintenance.StartMergeOperation().Join();
    for (var i = 0; i < n; ++i)
    {
      var result = data.TryGet(i, out var v);
      Assert.That(result, Is.True);
      Assert.That(v, Is.EqualTo((i + i).ToString()));
      Assert.That(data.ContainsKey(i), Is.True);
    }
    Assert.That(data.TryGet(n + 1, out var _), Is.False);
    data.Maintenance.Drop();
  }

  [Test]
  public void IntStringDeleteTest()
  {
    var dataPath = "data/IntStringDeleteTest";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var data = new ZoneTreeFactory<int, string>()
        .SetDataDirectory(dataPath)
        .OpenOrCreate();
    data.TryAtomicAdd(1, "1", out _);
    data.TryAtomicAdd(2, "2", out _);
    data.TryAtomicAdd(3, "3", out _);
    data.TryDelete(2, out _);
    Assert.That(data.ContainsKey(1), Is.True);
    Assert.That(data.ContainsKey(2), Is.False);
    Assert.That(data.ContainsKey(3), Is.True);
  }

  [Test]
  public void IntNullableIntDeleteTest()
  {
    var dataPath = "data/IntStringDeleteTest";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var data = new ZoneTreeFactory<int, int?>()
        .SetDataDirectory(dataPath)
        .SetValueSerializer(new NullableInt32Serializer())
        .OpenOrCreate();
    data.TryAtomicAdd(1, 1, out _);
    data.TryAtomicAdd(2, 2, out _);
    data.TryAtomicAdd(3, 3, out _);
    data.TryDelete(2, out _);
    Assert.That(data.ContainsKey(1), Is.True);
    Assert.That(data.ContainsKey(2), Is.False);
    Assert.That(data.ContainsKey(3), Is.True);
  }

  [Test]
  public void IntStringGarbageCollectionTest()
  {
    var dataPath = "data/IntStringGarbageCollectionTest";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    // load and populate tree
    {
      using var data = new ZoneTreeFactory<int, string>()
          .SetDataDirectory(dataPath)
          .OpenOrCreate();
      data.TryAtomicAdd(1, "1", out _);
      data.TryAtomicAdd(2, "2", out _);
      data.TryAtomicAdd(3, "3", out _);
      data.TryDelete(2, out _);
      data.TryAtomicAdd(4, "4", out _);
      data.TryAtomicUpdate(3, "33", out _);
      data.TryDelete(2, out _);
      Assert.That(data.ContainsKey(1), Is.True);
      Assert.That(data.ContainsKey(2), Is.False);
      Assert.That(data.ContainsKey(3), Is.True);
      Assert.That(data.ContainsKey(4), Is.True);
      data.TryGet(3, out var value3);
      Assert.That(value3, Is.EqualTo("33"));
      Assert.That(data.Maintenance.MutableSegment.Length, Is.EqualTo(4));
    }

    // reload tree and check the length
    for (var i = 0; i < 3; ++i)
    {
      using var data = new ZoneTreeFactory<int, string>()
          .Configure(options => options.EnableSingleSegmentGarbageCollection = true)
          .SetDataDirectory(dataPath)
          .Open();
      Assert.That(data.ContainsKey(1), Is.True);
      Assert.That(data.ContainsKey(2), Is.False);
      Assert.That(data.ContainsKey(3), Is.True);
      Assert.That(data.ContainsKey(4), Is.True);
      data.TryGet(3, out var value3);
      Assert.That(value3, Is.EqualTo("33"));
      Assert.That(data.Maintenance.MutableSegment.Length, Is.EqualTo(3));
    }
  }

  [Test]
  public void IntStringReadOnlySegmentLoadingTest()
  {
    var dataPath = "data/IntStringReadOnlySegmentLoadingTest";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    // load and populate tree
    {
      using var data = new ZoneTreeFactory<int, string>()
          .SetDataDirectory(dataPath)
          .OpenOrCreate();
      data.TryAtomicAdd(1, "1", out _);
      data.TryAtomicAdd(2, "2", out _);
      data.TryAtomicAdd(3, "3", out _);
      data.TryDelete(2, out _);
      data.TryAtomicAdd(4, "4", out _);
      data.TryAtomicUpdate(3, "33", out _);
      data.TryDelete(2, out _);
      Assert.That(data.ContainsKey(1), Is.True);
      Assert.That(data.ContainsKey(2), Is.False);
      Assert.That(data.ContainsKey(3), Is.True);
      Assert.That(data.ContainsKey(4), Is.True);
      data.TryGet(3, out var value3);
      Assert.That(value3, Is.EqualTo("33"));
      Assert.That(data.Maintenance.MutableSegment.Length, Is.EqualTo(4));
      data.Maintenance.MoveMutableSegmentForward();
      Assert.That(data.Maintenance.ReadOnlySegments[0].Length, Is.EqualTo(4));
    }

    // reload tree and check the length
    for (var i = 0; i < 3; ++i)
    {
      using var data = new ZoneTreeFactory<int, string>()
          .SetDataDirectory(dataPath)
          .Open();
      Assert.That(data.ContainsKey(1), Is.True);
      Assert.That(data.ContainsKey(2), Is.False);
      Assert.That(data.ContainsKey(3), Is.True);
      Assert.That(data.ContainsKey(4), Is.True);
      data.TryGet(3, out var value3);
      Assert.That(value3, Is.EqualTo("33"));
      Assert.That(data.Maintenance.ReadOnlySegments[0].Length, Is.EqualTo(4));
    }
  }

  [Test]
  public void LoadedReadOnlySegmentOwnsItsWal()
  {
    var dataPath = "data/LoadedReadOnlySegmentOwnsItsWal";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    long readOnlySegmentId;
    {
      using var data = new ZoneTreeFactory<int, string>()
          .SetDataDirectory(dataPath)
          .OpenOrCreate();
      data.TryAtomicAdd(1, "1", out _);
      data.Maintenance.MoveMutableSegmentForward();
      readOnlySegmentId = data.Maintenance.ReadOnlySegments[0].SegmentId;
    }

    var segmentWalDirectory = Path.Combine(dataPath, "seg");
    string[] ReadOnlySegmentWalFiles() =>
        Directory.GetFiles(segmentWalDirectory, readOnlySegmentId + ".*");
    Assert.That(ReadOnlySegmentWalFiles(), Is.Not.Empty);

    // A loaded read-only segment must keep its WAL so it can close
    // the file on dispose and delete it when the segment is dropped.
    {
      using var data = new ZoneTreeFactory<int, string>()
          .SetDataDirectory(dataPath)
          .Open();
      data.Maintenance.StartMergeOperation().Join();
      Assert.That(data.Maintenance.ReadOnlySegments, Is.Empty);
      Assert.That(ReadOnlySegmentWalFiles(), Is.Empty);
    }
  }

  [Test]
  public void IntStringDiskSegmentLoadingTest()
  {
    var dataPath = "data/IntStringGarbageCollectionTest";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    // load and populate tree
    {
      using var data = new ZoneTreeFactory<int, string>()
          .SetDataDirectory(dataPath)
          .OpenOrCreate();
      data.TryAtomicAdd(1, "1", out _);
      data.TryAtomicAdd(2, "2", out _);
      data.TryAtomicAdd(3, "3", out _);
      data.TryDelete(2, out _);
      Assert.That(data.ContainsKey(1), Is.True);
      Assert.That(data.ContainsKey(2), Is.False);
      Assert.That(data.ContainsKey(3), Is.True);
      Assert.That(data.Maintenance.MutableSegment.Length, Is.EqualTo(3));
      data.Maintenance.MoveMutableSegmentForward();
      Assert.That(data.Maintenance.ReadOnlySegments[0].Length, Is.EqualTo(3));
      data.Maintenance.StartMergeOperation().Join();
      Assert.That(data.Maintenance.DiskSegment.Length, Is.EqualTo(2));
    }

    // reload tree and check the length
    for (var i = 0; i < 3; ++i)
    {
      using var data = new ZoneTreeFactory<int, string>()
          .SetDataDirectory(dataPath)
          .Open();
      Assert.That(data.ContainsKey(1), Is.True);
      Assert.That(data.ContainsKey(2), Is.False);
      Assert.That(data.ContainsKey(3), Is.True);
      Assert.That(data.Maintenance.DiskSegment.Length, Is.EqualTo(2));
    }
  }

  [TestCase(true)]
  [TestCase(false)]
  public void StringIntTreeTest(bool useSparseArray)
  {
    var dataPath = "data/StringIntTreeTest." + useSparseArray;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var data = new ZoneTreeFactory<string, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(5)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetKeySerializer(new UnicodeStringSerializer())
        .OpenOrCreate();
    int n = 2000;
    for (var i = 0; i < n; ++i)
    {
      data.Upsert(i.ToString(), i + i);
    }
    data.Maintenance.MoveMutableSegmentForward();
    data.Maintenance.StartMergeOperation().Join();
    if (useSparseArray)
      data.Maintenance.DiskSegment.InitSparseArray(200);
    for (var i = 0; i < n; ++i)
    {
      var result = data.TryGet(i.ToString(), out var v);
      Assert.That(result, Is.True);
      Assert.That(v, Is.EqualTo(i + i));
      Assert.That(data.ContainsKey(i.ToString()), Is.True);
    }
    Assert.That(data.TryGet(n + 1.ToString(), out var _), Is.False);
    data.Maintenance.Drop();
  }

  [TestCase(true)]
  [TestCase(false)]
  public void StringStringTreeTest(bool useSparseArray)
  {
    var dataPath = "data/StringStringTreeTest." + useSparseArray;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var data = new ZoneTreeFactory<string, string>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(5)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetKeySerializer(new UnicodeStringSerializer())
        .SetValueSerializer(new UnicodeStringSerializer())
        .OpenOrCreate();
    int n = 2000;
    for (var i = 0; i < n; ++i)
    {
      data.Upsert(i.ToString(), (i + i).ToString());
    }
    data.Maintenance.MoveMutableSegmentForward();
    data.Maintenance.StartMergeOperation().Join();
    if (useSparseArray)
      data.Maintenance.DiskSegment.InitSparseArray(200);
    for (var i = 0; i < n; ++i)
    {
      var result = data.TryGet(i.ToString(), out var v);
      Assert.That(result, Is.True);
      Assert.That(v, Is.EqualTo((i + i).ToString()));
      Assert.That(data.ContainsKey(i.ToString()), Is.True);
    }
    Assert.That(data.TryGet(n + 1.ToString(), out var _), Is.False);
    data.Maintenance.Drop();
  }

  static void ReloadIntIntTreeTestHelper(string dataPath, bool destroy)
  {
    using var data = new ZoneTreeFactory<int, int>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(5)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureWriteAheadLogOptions(x =>
            x.WriteAheadLogMode = WriteAheadLogMode.Sync)
        .SetIsDeletedDelegate((in int key, in int value) => value == -1)
        .SetMarkValueDeletedDelegate((ref int value) => value = -1)
        .OpenOrCreate();
    var n = 2000;
    var deleted = new HashSet<int>() { 11, 99, 273, 200, 333, 441, 203, 499, 666 };
    for (var i = 0; i < n; ++i)
    {
      data.Upsert(i, i + i);
      if (i == 500)
        data.Maintenance.StartMergeOperation().Join();
    }
    foreach (var del in deleted) data.ForceDelete(del);

    for (var i = 0; i < n; ++i)
    {
      if (deleted.Contains(i))
      {
        var result = data.TryGet(i, out var v);
        Assert.That(result, Is.False);
      }
      else
      {
        var result = data.TryGet(i, out var v);
        Assert.That(result, Is.True);
        Assert.That(v, Is.EqualTo(i + i));
        Assert.That(data.ContainsKey(i), Is.True);
      }
    }
    Assert.That(data.Count(), Is.EqualTo(n - deleted.Count));
    if (destroy)
      data.Maintenance.Drop();
  }
  [Test]
  public void ReloadIntIntTreeTest()
  {
    var dataPath = "data/ReloadIntIntTreeTest";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    for (var i = 0; i < 10; ++i)
    {
      ReloadIntIntTreeTestHelper(dataPath, false);
    }
    ReloadIntIntTreeTestHelper(dataPath, true);

  }
}
