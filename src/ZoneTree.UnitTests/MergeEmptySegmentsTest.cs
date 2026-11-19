using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class MergeEmptySegmentsTest
{
  [TestCase(DiskSegmentMode.SingleDiskSegment, 33, 77)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment, 33, 77)]
  public void MergeEmptySegments(
      DiskSegmentMode diskSegmentMode,
      int minimumRecordCount,
      int maximumRecordCount)
  {
    var dataPath = "data/MergeEmptySegments" + diskSegmentMode + minimumRecordCount + maximumRecordCount;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetIsDeletedDelegate((in int key, in int value) => true)
        .SetMarkValueDeletedDelegate((ref int value) => value = -1)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(100)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureDiskSegmentOptions(x =>
        {
          x.Mode = diskSegmentMode;
          if (minimumRecordCount > 0)
            x.MinimumRecordCount = minimumRecordCount;
          if (maximumRecordCount > 0)
            x.MaximumRecordCount = maximumRecordCount;
        })
        .OpenOrCreate();
    var recordCount = 3000;
    for (var i = 0; i < recordCount; ++i)
      zoneTree.Upsert(i, i);
    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation()?.Join();
    Assert.That(zoneTree.Maintenance.DiskSegment.Length, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.MutableSegment.Length, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.InMemoryRecordCount, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.TotalRecordCount, Is.EqualTo(0));

    for (var i = 0; i < recordCount; ++i)
    {
      var hasRecord = zoneTree.TryGet(i, out var _);
      if (hasRecord)
        Assert.That(hasRecord, Is.False);
    }

    zoneTree.Maintenance.SaveMetadata();
    zoneTree.Maintenance.Drop();
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment, 33, 77)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment, 33, 77)]
  public void MergeDeletedSegments(
      DiskSegmentMode diskSegmentMode,
      int minimumRecordCount,
      int maximumRecordCount)
  {
    var dataPath = "data/MergeEmptySegments" + diskSegmentMode + minimumRecordCount + maximumRecordCount;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    var isRecordDeleted = false;
    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetIsDeletedDelegate((in int key, in int value) => isRecordDeleted)
        .SetMarkValueDeletedDelegate((ref int value) => value = -1)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(100)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureDiskSegmentOptions(x =>
        {
          x.Mode = diskSegmentMode;
          if (minimumRecordCount > 0)
            x.MinimumRecordCount = minimumRecordCount;
          if (maximumRecordCount > 0)
            x.MaximumRecordCount = maximumRecordCount;
        })
        .OpenOrCreate();
    var recordCount = 3000;
    for (var i = 0; i < recordCount; ++i)
      zoneTree.Upsert(i, i);
    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation()?.Join();
    Assert.That(zoneTree.Maintenance.DiskSegment.Length, Is.EqualTo(recordCount));
    Assert.That(zoneTree.Maintenance.MutableSegment.Length, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.InMemoryRecordCount, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.TotalRecordCount, Is.EqualTo(recordCount));

    isRecordDeleted = true;
    for (var i = 0; i < recordCount; ++i)
      zoneTree.Upsert(i, i);

    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation()?.Join();

    Assert.That(zoneTree.Maintenance.DiskSegment.Length, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.MutableSegment.Length, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.InMemoryRecordCount, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.TotalRecordCount, Is.EqualTo(0));

    for (var i = 0; i < recordCount; ++i)
    {
      var hasRecord = zoneTree.TryGet(i, out var _);
      if (hasRecord)
        Assert.That(hasRecord, Is.False);
    }

    zoneTree.Maintenance.SaveMetadata();
    zoneTree.Maintenance.Drop();
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment, 33, 77)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment, 33, 77)]
  public void MergeDeletedBottomSegments(
  DiskSegmentMode diskSegmentMode,
  int minimumRecordCount,
  int maximumRecordCount)
  {
    var dataPath = "data/MergeEmptySegments" + diskSegmentMode + minimumRecordCount + maximumRecordCount;
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    var isRecordDeleted = false;
    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetIsDeletedDelegate((in int key, in int value) => isRecordDeleted)
        .SetMarkValueDeletedDelegate((ref int value) => value = -1)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(100)
        .SetDiskSegmentMaxItemCount(250)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureDiskSegmentOptions(x =>
        {
          x.Mode = diskSegmentMode;
          if (minimumRecordCount > 0)
            x.MinimumRecordCount = minimumRecordCount;
          if (maximumRecordCount > 0)
            x.MaximumRecordCount = maximumRecordCount;
        })
        .OpenOrCreate();
    var recordCount = 3000;
    for (var i = 0; i < recordCount; ++i)
      zoneTree.Upsert(i, i);
    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation()?.Join();
    zoneTree.Maintenance.StartBottomSegmentsMergeOperation(0, int.MaxValue)?.Join();
    Assert.That(zoneTree.Maintenance.DiskSegment.Length, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.BottomSegments.Count, Is.EqualTo(1));
    Assert.That(zoneTree.Maintenance.BottomSegments[0].Length, Is.EqualTo(recordCount));
    Assert.That(zoneTree.Maintenance.MutableSegment.Length, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.InMemoryRecordCount, Is.EqualTo(0));

    isRecordDeleted = true;
    for (var i = 0; i < recordCount; ++i)
      zoneTree.Upsert(i, i);

    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation()?.Join();
    zoneTree.Maintenance.StartBottomSegmentsMergeOperation(0, int.MaxValue)?.Join();

    Assert.That(zoneTree.Maintenance.DiskSegment.Length, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.BottomSegments.Count, Is.EqualTo(1));
    Assert.That(zoneTree.Maintenance.BottomSegments[0].Length, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.MutableSegment.Length, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.InMemoryRecordCount, Is.EqualTo(0));
    Assert.That(zoneTree.Maintenance.TotalRecordCount, Is.EqualTo(0));

    for (var i = 0; i < recordCount; ++i)
    {
      var hasRecord = zoneTree.TryGet(i, out var _);
      if (hasRecord)
        Assert.That(hasRecord, Is.False);
    }
    zoneTree.Maintenance.SaveMetadata();
    zoneTree.Maintenance.Drop();
  }
}
