using ZoneTree.Comparers;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.UnitTests;

public sealed class CountTests
{
  [TestCase(DiskSegmentMode.MultiPartDiskSegment, 33, 77, false)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment, 33, 77, true)]
  public void CountRecordsDuringAMerge(
      DiskSegmentMode diskSegmentMode,
      int minimumRecordCount,
      int maximumRecordCount,
      bool useFullScan)
  {
    var dataPath = "data/CountRecordsDuringAMerge" + diskSegmentMode + minimumRecordCount + maximumRecordCount + useFullScan;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<string, int>()
        .SetIsDeletedDelegate((in string key, in int value) => value == -1)
        .SetMarkValueDeletedDelegate((ref int x) => x = -1)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(100)
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
    var recordCount = 300;
    PrepareData1(zoneTree, recordCount, "a");
    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation()?.Join();
    var stepSize = 37;
    PrepareData1(zoneTree, recordCount);
    var deletedCount = DeleteData1(zoneTree, recordCount, stepSize);

    var expectedCount = recordCount * 8 - deletedCount;
    var failedCount = 0;

    var mergeThread = zoneTree.Maintenance.StartMergeOperation();

    var isMergeFinished = false;
    var task = Task.Factory.StartNew(() =>
    {
      while (!isMergeFinished)
      {
        var actualCount = useFullScan ? zoneTree.CountFullScan() : zoneTree.Count();
        if (actualCount != expectedCount)
          ++failedCount;
      }
    });
    mergeThread?.Join();
    isMergeFinished = true;
    task.Wait();
    zoneTree.Maintenance.Drop();

    Assert.That(failedCount, Is.EqualTo(0));
  }

  static void PrepareData1(IZoneTree<string, int> zoneTree, int n, string postfix = "")
  {
    for (var i = 0; i < n; ++i)
    {
      var key = $"myprefix1|string{i}{postfix}";
      zoneTree.Upsert(key, i);
    }
    for (var i = 0; i < n; ++i)
    {
      var key = $"myprefix2|string{i}{postfix}";
      zoneTree.Upsert(key, i);
    }
    for (var i = 0; i < n; ++i)
    {
      var key = $"myprefix3|string{i}{postfix}";
      zoneTree.Upsert(key, i);
    }
    for (var i = 0; i < n; ++i)
    {
      var key = $"myprefix4|string{i}{postfix}";
      zoneTree.Upsert(key, i);
    }
  }

  static int DeleteData1(IZoneTree<string, int> zoneTree, int n, int step)
  {
    var deletedCount = 0;
    for (var i = step; i < n; i += step)
    {
      var key = $"myprefix1|string{i}";
      zoneTree.ForceDelete(key);
      ++deletedCount;

    }
    for (var i = step; i < n; i += step)
    {
      var key = $"myprefix2|string{i}";
      zoneTree.ForceDelete(key);
      ++deletedCount;
    }
    for (var i = step; i < n; i += step)
    {
      var key = $"myprefix3|string{i}";
      zoneTree.ForceDelete(key);
      ++deletedCount;
    }
    for (var i = step; i < n; i += step)
    {
      var key = $"myprefix4|string{i}";
      zoneTree.ForceDelete(key);
      ++deletedCount;
    }
    return deletedCount;
  }
}
