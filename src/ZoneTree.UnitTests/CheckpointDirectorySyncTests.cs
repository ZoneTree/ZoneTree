using ZoneTree.AbstractFileStream;
using ZoneTree.Core;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class CheckpointDirectorySyncTests
{
  static ZoneTreeFactory<int, int> Factory(IFileStreamProvider files,
      WriteAheadLogMode mode, DiskSegmentMode diskMode)
      => new ZoneTreeFactory<int, int>(files)
          .SetDataDirectory("data")
          .SetLogger(SilentTestLogger.Instance)
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .SetMutableSegmentMaxItemCount(32)
          .ConfigureDiskSegmentOptions(options =>
          {
            options.DiskSegmentMode = diskMode;
            options.MinimumRecordCount = 8;
            options.MaximumRecordCount = 16;
          })
          .ConfigureWriteAheadLogOptions(options =>
          {
            options.WriteAheadLogMode = mode;
            options.CompressionBlockSize = 256;
            options.SharedModeOptions.MaximumChunkSize = 1024;
            options.SyncCompressedModeOptions.EnableTailWriterJob = false;
          });

  [TestCase(WriteAheadLogMode.Shared, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(WriteAheadLogMode.Shared, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(WriteAheadLogMode.AsyncShared, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(WriteAheadLogMode.AsyncShared, DiskSegmentMode.MultiPartDiskSegment)]
  public async Task FailedOutputDirectorySyncKeepsTheWalCheckpoint(
      WriteAheadLogMode mode, DiskSegmentMode diskMode)
  {
    var files = new PowerLossFileStreamProvider();
    using var tree = Factory(files, mode, diskMode).Create();
    for (var key = 0; key < 120; ++key) tree.Upsert(key, key + 1);
    await tree.SyncWALAsync();
    tree.Maintenance.MoveMutableSegmentForward();
    var segments = tree.Maintenance.ReadOnlySegments.Select(segment => segment.SegmentId).ToArray();
    var chunks = files.GetFiles("data/seg/shared").Order().ToArray();
    var result = MergeResult.SUCCESS;
    tree.Maintenance.OnMergeOperationEnded += (_, value) => result = value;
    files.BeforeDirectorySync = path =>
    {
      if (path == "data") throw new IOException("Injected checkpoint directory sync failure.");
    };
    try { tree.Maintenance.StartMergeOperation().Join(); }
    finally { files.BeforeDirectorySync = null; }

    Assert.That(result, Is.EqualTo(MergeResult.FAILURE));
    Assert.That(tree.Maintenance.ReadOnlySegments.Select(segment => segment.SegmentId), Is.EqualTo(segments));
    Assert.That(files.GetFiles("data/seg/shared").Order(), Is.EqualTo(chunks));
    using var recovered = Factory(files.SimulatePowerLoss(), mode, diskMode).Open();
    AssertRecords(recovered, 120);
    tree.Maintenance.StartMergeOperation().Join();
    Assert.That(result, Is.EqualTo(MergeResult.SUCCESS));
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public async Task FailedBottomDirectorySyncKeepsTheOldDiskSegments(DiskSegmentMode diskMode)
  {
    const WriteAheadLogMode mode = WriteAheadLogMode.Shared;
    var files = new PowerLossFileStreamProvider();
    using var tree = Factory(files, mode, diskMode).SetDiskSegmentMaxItemCount(8).Create();
    for (var batch = 0; batch < 2; ++batch)
    {
      for (var key = batch * 64; key < (batch + 1) * 64; ++key) tree.Upsert(key, key + 1);
      await tree.SyncWALAsync();
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
    }
    var segments = tree.Maintenance.BottomSegments.Select(segment => segment.SegmentId).ToArray();
    Assert.That(segments.Length, Is.EqualTo(2));
    var result = MergeResult.SUCCESS;
    tree.Maintenance.OnBottomSegmentsMergeOperationEnded += (_, value) => result = value;
    files.BeforeDirectorySync = path =>
    {
      if (path == "data") throw new IOException("Injected bottom checkpoint directory sync failure.");
    };
    try { tree.Maintenance.StartBottomSegmentsMergeOperation(0, 1).Join(); }
    finally { files.BeforeDirectorySync = null; }

    Assert.That(result, Is.EqualTo(MergeResult.FAILURE));
    Assert.That(tree.Maintenance.BottomSegments.Select(segment => segment.SegmentId), Is.EqualTo(segments));
    AssertRecords(tree, 128);
    using var recovered = Factory(files.SimulatePowerLoss(), mode, diskMode).Open();
    AssertRecords(recovered, 128);
    tree.Maintenance.StartBottomSegmentsMergeOperation(0, 1).Join();
    Assert.That(result, Is.EqualTo(MergeResult.SUCCESS));
  }

  [Test]
  public async Task FailedMetadataDirectorySyncKeepsThePreviousGenerationsWal()
  {
    const WriteAheadLogMode mode = WriteAheadLogMode.Shared;
    const DiskSegmentMode diskMode = DiskSegmentMode.SingleDiskSegment;
    var files = new PowerLossFileStreamProvider();
    using var tree = Factory(files, mode, diskMode).Create();
    var oldMetadata = files.ReadAllBytes("data/0.json");
    for (var key = 0; key < 120; ++key) tree.Upsert(key, key + 1);
    await tree.SyncWALAsync();
    tree.Maintenance.MoveMutableSegmentForward();
    // The move records the next mutable segment in the background.
    ((ZoneTree<int, int>)tree).WaitForNextMutableSegment();
    var oldMetaWal = files.ReadAllBytes("data/0.meta.wal");
    Assert.That(oldMetaWal, Is.Not.Empty);
    files.BeforeDirectorySync = path =>
    {
      if (path == "data") throw new IOException("Injected metadata directory sync failure.");
    };
    try { Assert.Throws<IOException>(tree.Maintenance.SaveMetaData); }
    finally { files.BeforeDirectorySync = null; }
    Assert.That(files.ReadAllBytes("data/0.meta.wal"), Is.EqualTo(oldMetaWal));

    var crash = files.SimulatePowerLoss();
    // Model a lost metadata rename: recovery needs the previous generation's WAL.
    using (var metadata = crash.CreateFileStream("data/0.json", FileMode.Create, FileAccess.Write, FileShare.None))
    {
      metadata.Write(oldMetadata);
      metadata.Flush(true);
    }
    using var recovered = Factory(crash, mode, diskMode).Open();
    AssertRecords(recovered, 120);
    // Continuing after the failed save must publish later rotations under the
    // generation of the metadata that did reach the OS, after retrying its sync.
    for (var key = 120; key < 180; ++key) tree.Upsert(key, key + 1);
    tree.Maintenance.MoveMutableSegmentForward();
    await tree.SyncWALAsync();
    using var continued = Factory(files.SimulatePowerLoss(), mode, diskMode).Open();
    AssertRecords(continued, 180);
    ((ZoneTree<int, int>)tree).WaitForNextMutableSegment();
    tree.Maintenance.SaveMetaData();
    Assert.That(files.ReadAllBytes("data/0.meta.wal"), Is.Empty);
  }

  static void AssertRecords(IZoneTree<int, int> tree, int count)
  {
    for (var key = 0; key < count; ++key)
    {
      Assert.That(tree.TryGet(key, out var value), Is.True, $"key {key}");
      Assert.That(value, Is.EqualTo(key + 1));
    }
  }
}
