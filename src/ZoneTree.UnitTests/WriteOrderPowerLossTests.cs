using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>
/// Explicit sync protects earlier writes across segment moves. Transactional
/// factories use a shared journal and recover a committed prefix; ordinary
/// per-segment WALs guarantee survival of synced writes across segment moves.
/// </summary>
public sealed class WriteOrderPowerLossTests
{
  static ZoneTreeFactory<int, int> CreateFactory(
      IFileStreamProvider provider, WriteAheadLogMode mode, int mutableSegmentMaxItemCount)
  {
    return new ZoneTreeFactory<int, int>(provider)
        .SetLogger(SilentTestLogger.Instance)
        .Configure(options =>
        {
          options.AllowUnsafeOptionValues = true;
        })
        .SetMutableSegmentMaxItemCount(mutableSegmentMaxItemCount)
        .ConfigureWriteAheadLogOptions(options =>
        {
          options.WriteAheadLogMode = mode;
          options.CompressionBlockSize = 256;
        });
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public void ExplicitlySyncedWritesSurviveLaterSegmentMoves(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    PowerLossFileStreamProvider afterPowerLoss;
    using (var tree = CreateFactory(provider, mode, 1000).Create())
    {
      for (var key = 0; key < 350; ++key)
      {
        // The value 0 is the deletion marker of the default int value.
        tree.Upsert(key, key + 1);
        if (key == 199)
          tree.SyncWALAsync().GetAwaiter().GetResult();
        if (key % 100 == 99)
          tree.Maintenance.MoveMutableSegmentForward();
      }
      afterPowerLoss = provider.SimulatePowerLoss();
    }
    using var recovered = CreateFactory(afterPowerLoss, mode, 1000).Open();
    var present = Enumerable.Range(0, 350).Where(key => recovered.TryGet(key, out _)).ToArray();
    Assert.That(Enumerable.Range(0, 200).Except(present), Is.Empty);
  }

  static int? ReadCommitted(ITransactionalZoneTree<int, int> tree, int key)
  {
    using var tx = tree.BeginReadOnlyTransaction();
    return tx.TryGet(key, out var value) ? value : null;
  }

  [TestCase(WriteAheadLogMode.Sync, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(WriteAheadLogMode.Sync, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(WriteAheadLogMode.SyncCompressed, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(WriteAheadLogMode.AsyncCompressed, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(WriteAheadLogMode.Shared, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(WriteAheadLogMode.AsyncShared, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(WriteAheadLogMode.None, DiskSegmentMode.SingleDiskSegment)]
  public async Task MergeKeepsTheValueAnUnsyncedCommitReplaces(
      WriteAheadLogMode mode, DiskSegmentMode diskSegmentMode)
  {
    var provider = new PowerLossFileStreamProvider();
    PowerLossFileStreamProvider afterPowerLoss;
    using (var tree = CreateFactory(provider, mode, 1000)
        .ConfigureDiskSegmentOptions(options => options.DiskSegmentMode = diskSegmentMode)
        .CreateTransactional())
    {
      var storage = tree.Maintenance.ZoneTree.Maintenance;
      using (var tx = tree.BeginTransaction())
      {
        tx.Upsert(1, 10);
        tx.Commit();
      }
      await tree.SyncWALAsync();
      storage.MoveMutableSegmentForward();
      storage.StartMergeOperation()?.Join();

      // The new version enters a frozen segment before its commit record is
      // appended. With shared WALs, merge publication syncs the whole journal;
      // with WAL disabled, the new status record can remain only in memory.
      using (var tx = tree.BeginTransaction())
      {
        tx.Upsert(1, 20);
        storage.MoveMutableSegmentForward();
        tx.Commit();
      }
      storage.StartMergeOperation()?.Join();
      afterPowerLoss = provider.SimulatePowerLoss();
    }
    using var recovered = CreateFactory(afterPowerLoss, mode, 1000).OpenTransactional();
    if (mode == WriteAheadLogMode.None)
      Assert.That(ReadCommitted(recovered, 1), Is.AnyOf(10, 20));
    else
      Assert.That(ReadCommitted(recovered, 1), Is.EqualTo(20),
          "publishing a shared-WAL merge synced the replacement's commit record too");
  }

  [Test]
  public void FailedExplicitSyncDoesNotAcknowledgeNewWrites()
  {
    var provider = new PowerLossFileStreamProvider();
    PowerLossFileStreamProvider afterPowerLoss;
    var tree = CreateFactory(provider, WriteAheadLogMode.Shared, 1000)
        .ConfigureWriteAheadLogOptions(options => options.SyncCompressedModeOptions.EnableTailWriterJob = false)
        .CreateTransactional();
    try
    {
      using (var tx = tree.BeginTransaction())
      {
        tx.Upsert(0, 100);
        tx.CommitDurableAsync().GetAwaiter().GetResult();
      }
      var storage = tree.Maintenance.ZoneTree.Maintenance;
      storage.MoveMutableSegmentForward();
      using (var tx = tree.BeginTransaction())
      {
        tx.Upsert(1, 10);
        tx.Upsert(2, 20);
        tx.Commit();
        provider.FailDiskSync = path => path.Contains("/seg/shared/", StringComparison.Ordinal) &&
            path.EndsWith(".wal", StringComparison.Ordinal);
        Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(() => tree.SyncWALAsync());
      }
      Assert.That(ReadCommitted(tree, 0), Is.EqualTo(100), "reads keep working");
      afterPowerLoss = provider.SimulatePowerLoss();
    }
    finally
    {
      provider.FailDiskSync = null;
      Assert.Throws<WriteAheadLogSyncFailedException>(tree.Dispose,
          "a failed shared journal must keep reporting its durability failure");
      Assert.That(provider.OpenStreamCount, Is.Zero, "failed disposal must release every file");
    }
    using var recovered = CreateFactory(afterPowerLoss, WriteAheadLogMode.Sync, 1000).OpenTransactional();
    Assert.That(ReadCommitted(recovered, 0), Is.EqualTo(100));
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public void RecoveryKeepsSyncedCommitsAfterUnsyncedWrites(WriteAheadLogMode mode)
  {
    const int count = 300;
    const int syncedCount = count / 2;
    var provider = new PowerLossFileStreamProvider();
    PowerLossFileStreamProvider afterPowerLoss;
    using (var tree = CreateFactory(provider, mode, 64).CreateTransactional())
    {
      for (var i = 0; i < count; ++i)
      {
        using var tx = tree.BeginTransaction();
        tx.Upsert(2 * i, i);
        tx.Upsert(2 * i + 1, i);
        tx.Commit();
        if (i == syncedCount - 1)
          tree.SyncWALAsync().GetAwaiter().GetResult();
      }
      var open = tree.BeginTransaction();
      open.Upsert(-1, -1);
      afterPowerLoss = provider.SimulatePowerLoss();
    }

    using (var recovered = CreateFactory(afterPowerLoss, mode, 64).OpenTransactional())
    {
      using (var reader = recovered.BeginReadOnlyTransaction())
      {
        for (var i = 0; i < syncedCount; ++i)
        {
          Assert.That(reader.ContainsKey(2 * i), Is.True, "an explicitly synced transaction must survive");
          Assert.That(reader.ContainsKey(2 * i + 1), Is.True);
        }
      }

      using var tx = recovered.BeginTransaction();
      tx.Upsert(-2, -2);
      tx.Commit();
    }
    using var reopened = CreateFactory(afterPowerLoss, mode, 64).OpenTransactional();
    using var check = reopened.BeginReadOnlyTransaction();
    Assert.That(check.ContainsKey(-2), Is.True);
    Assert.That(check.ContainsKey(2 * (syncedCount - 1)), Is.True);
  }
}
