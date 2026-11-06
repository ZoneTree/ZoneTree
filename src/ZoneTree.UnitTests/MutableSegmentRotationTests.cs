using ZoneTree.AbstractFileStream;
using ZoneTree.Core;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>
/// Moving the mutable segment forward publishes a next segment that was recorded in the meta
/// WAL ahead of time, so writers do not wait for the meta WAL.
/// </summary>
public sealed class MutableSegmentRotationTests
{
  static ZoneTreeFactory<int, int> Factory(IFileStreamProvider files, WriteAheadLogMode mode)
      => new ZoneTreeFactory<int, int>(files)
          .SetDataDirectory("data")
          .SetLogger(SilentTestLogger.Instance)
          .ConfigureWriteAheadLogOptions(options =>
          {
            options.WriteAheadLogMode = mode;
            options.SyncCompressedModeOptions.EnableTailWriterJob = false;
          });

  static ZoneTree<int, int> Engine(IZoneTree<int, int> tree) => (ZoneTree<int, int>)tree;

  static void AssertRecords(IZoneTree<int, int> tree, int count)
  {
    for (var key = 0; key < count; ++key)
      Assert.That(tree.TryGet(key, out var value) && value == key + 1, Is.True, $"key {key}");
  }

  [TestCase(WriteAheadLogMode.SyncCompressed, false)]
  [TestCase(WriteAheadLogMode.SyncCompressed, true)]
  [TestCase(WriteAheadLogMode.Shared, false)]
  [TestCase(WriteAheadLogMode.Shared, true)]
  public async Task APowerLossKeepsTheRecordsOfARecordedNextSegment(
      WriteAheadLogMode mode, bool saveMetadata)
  {
    var files = new PowerLossFileStreamProvider();
    using var tree = Factory(files, mode).Create();
    for (var key = 0; key < 10; ++key) tree.Upsert(key, key + 1);
    tree.Maintenance.MoveMutableSegmentForward();
    Engine(tree).WaitForNextMutableSegment();
    for (var key = 10; key < 20; ++key) tree.Upsert(key, key + 1);
    // A metadata save after the next segment was recorded keeps it recorded.
    if (saveMetadata)
      tree.Maintenance.SaveMetaData();
    // This move publishes the recorded segment, and the writes after it go to its WAL.
    tree.Maintenance.MoveMutableSegmentForward();
    for (var key = 20; key < 30; ++key) tree.Upsert(key, key + 1);
    await tree.SyncWALAsync();
    using var recovered = Factory(files.SimulatePowerLoss(), mode).Open();
    AssertRecords(recovered, 30);
  }

  [Test]
  public void AMoveDoesNotWaitForTheMetaWal()
  {
    var files = new PowerLossFileStreamProvider();
    using var tree = Factory(files, WriteAheadLogMode.Shared).Create();
    tree.Upsert(0, 1);
    tree.Maintenance.MoveMutableSegmentForward();
    Engine(tree).WaitForNextMutableSegment();
    tree.Upsert(1, 2);
    using var metaWalSync = new ManualResetEventSlim();
    files.BeforeDiskSync = path =>
    {
      if (path.Contains("meta", StringComparison.Ordinal))
        metaWalSync.Wait();
    };
    try
    {
      // The meta WAL cannot sync, yet the move completes and writes go on.
      var move = Task.Run(tree.Maintenance.MoveMutableSegmentForward);
      Assert.That(move.Wait(TimeSpan.FromSeconds(10)), Is.True);
      tree.Upsert(2, 3);
    }
    finally
    {
      metaWalSync.Set();
      files.BeforeDiskSync = null;
    }
    Engine(tree).WaitForNextMutableSegment();
    AssertRecords(tree, 3);
  }

  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.Shared)]
  public void TheOpIndexKeepsGrowingAfterAMergeDroppedTheSegmentWals(WriteAheadLogMode mode)
  {
    var files = new InMemoryFileStreamProvider();
    long last;
    using (var tree = Factory(files, mode).Create())
    {
      for (var key = 0; key < 100; ++key) tree.Upsert(key, key + 1);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
      Assert.That(tree.Maintenance.ReadOnlySegments, Is.Empty);
      last = Engine(tree).LastOpIndex;
      Assert.That(last, Is.GreaterThanOrEqualTo(100));
    }
    using var reopened = Factory(files, mode).Open();
    Assert.That(Engine(reopened).LastOpIndex, Is.GreaterThanOrEqualTo(last));
    AssertRecords(reopened, 100);
  }
}
