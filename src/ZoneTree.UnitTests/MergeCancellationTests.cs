using ZoneTree.Core;

namespace ZoneTree.UnitTests;

public sealed class MergeCancellationTests
{
  // OnMergeOperationStarted and OnBottomSegmentsMergeOperationStarted run on the
  // caller thread before the merge thread starts. Cancelling from them always
  // happens before the merge thread runs, which is the race window that
  // "start a merge, then cancel it" usually hits.

  [Test]
  public void MergeHonorsCancellationRequestedBeforeMergeThreadStarts()
  {
    var dataPath = "data/MergeHonorsCancellationRequestedBeforeMergeThreadStarts";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .OpenOrCreate();
    var maintenance = zoneTree.Maintenance;
    for (var i = 0; i < 1000; ++i)
      zoneTree.Upsert(i, i);
    maintenance.MoveMutableSegmentForward();

    MergeResult? result = null;
    maintenance.OnMergeOperationStarted += _ => maintenance.TryCancelMergeOperation();
    maintenance.OnMergeOperationEnded += (_, mergeResult) => result = mergeResult;
    maintenance.StartMergeOperation().Join();

    Assert.That(result, Is.EqualTo(MergeResult.CANCELLED_BY_USER));
    Assert.That(maintenance.ReadOnlySegments, Has.Count.EqualTo(1));
  }

  [Test]
  public void BottomSegmentsMergeHonorsCancellationRequestedBeforeMergeThreadStarts()
  {
    var dataPath = "data/BottomSegmentsMergeHonorsCancellationRequestedBeforeMergeThreadStarts";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    const int diskSegmentMaxItemCount = 10;
    using var zoneTree = new ZoneTreeFactory<int, int>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDataDirectory(dataPath)
        .SetDiskSegmentMaxItemCount(diskSegmentMaxItemCount)
        .OpenOrCreate();
    var maintenance = zoneTree.Maintenance;
    // A merged disk segment larger than DiskSegmentMaxItemCount becomes a bottom segment.
    for (var i = 1; i <= diskSegmentMaxItemCount + 1; ++i)
      zoneTree.Upsert(i, i);
    maintenance.MoveMutableSegmentForward();
    maintenance.StartMergeOperation().Join();
    Assert.That(maintenance.BottomSegments, Has.Count.EqualTo(1));

    MergeResult? result = null;
    maintenance.OnBottomSegmentsMergeOperationStarted += _ => maintenance.TryCancelBottomSegmentsMergeOperation();
    maintenance.OnBottomSegmentsMergeOperationEnded += (_, mergeResult) => result = mergeResult;
    maintenance.StartBottomSegmentsMergeOperation(0, int.MaxValue).Join();

    Assert.That(result, Is.EqualTo(MergeResult.CANCELLED_BY_USER));
    Assert.That(maintenance.BottomSegments, Has.Count.EqualTo(1));
  }
}
