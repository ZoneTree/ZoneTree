using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>
/// A power loss may drop unsynced writes, but with
/// <see cref="ZoneTreeOptions{TKey, TValue}.PreserveWriteOrderAcrossSegments"/>
/// the writes that survive are a prefix of the write order.
/// </summary>
public sealed class WriteOrderPowerLossTests
{
  static ZoneTreeFactory<int, int> CreateFactory(
      IFileStreamProvider provider, WriteAheadLogMode mode, int mutableSegmentMaxItemCount)
  {
    return new ZoneTreeFactory<int, int>(provider)
        .SetLogger(new ConsoleLogger(LogLevel.Error))
        .Configure(options =>
        {
          options.AllowUnsafeOptionValues = true;
          options.PreserveWriteOrderAcrossSegments = true;
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
  public void RecoveredWritesAreAPrefixOfTheWriteOrder(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    PowerLossFileStreamProvider afterPowerLoss;
    using (var tree = CreateFactory(provider, mode, 1000).Create())
    {
      for (var key = 0; key < 350; ++key)
      {
        // The value 0 is the deletion marker of the default int value.
        tree.Upsert(key, key + 1);
        if (key % 100 == 99)
          tree.Maintenance.MoveMutableSegmentForward();
      }
      afterPowerLoss = provider.SimulatePowerLoss();
    }
    using var recovered = CreateFactory(afterPowerLoss, mode, 1000).Open();
    var present = Enumerable.Range(0, 350).Where(key => recovered.TryGet(key, out _)).ToArray();
    Assert.That(present, Is.EqualTo(Enumerable.Range(0, present.Length)));
    // Every moved segment was synced before the next one accepted writes.
    Assert.That(present.Length, Is.GreaterThanOrEqualTo(300));
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

      // The new version reaches the merge, but its commit record stays in the
      // unsynced mutable segment.
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
    Assert.That(ReadCommitted(recovered, 1), Is.AnyOf(10, 20));
  }

  [Test]
  public void FailedRolloverSyncStopsWrites()
  {
    var provider = new PowerLossFileStreamProvider();
    PowerLossFileStreamProvider afterPowerLoss;
    using (var tree = CreateFactory(provider, WriteAheadLogMode.Sync, 1000).CreateTransactional())
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
        var segmentId = storage.MutableSegment.SegmentId;
        provider.FailDiskSync = path => path.Contains($"/seg/{segmentId}.", StringComparison.Ordinal);
        storage.MoveMutableSegmentForward();
        // The next write could reach the disk before the unsynced first one.
        Assert.Throws<WriteOrderBarrierFailedException>(() => tx.Upsert(2, 20));
        Assert.That(tx.State, Is.EqualTo(Transactional.TransactionState.Aborted));
      }
      Assert.That(ReadCommitted(tree, 0), Is.EqualTo(100), "reads keep working");
      afterPowerLoss = provider.SimulatePowerLoss();
      provider.FailDiskSync = null;
    }
    using var recovered = CreateFactory(afterPowerLoss, WriteAheadLogMode.Sync, 1000).OpenTransactional();
    Assert.That(ReadCommitted(recovered, 0), Is.EqualTo(100));
    Assert.That(ReadCommitted(recovered, 1), Is.Null);
    Assert.That(ReadCommitted(recovered, 2), Is.Null);
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public void RecoveredCommitsAreAPrefixOfTheCommitOrder(WriteAheadLogMode mode)
  {
    const int count = 300;
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
      }
      var open = tree.BeginTransaction();
      open.Upsert(-1, -1);
      afterPowerLoss = provider.SimulatePowerLoss();
    }

    int committed;
    using (var recovered = CreateFactory(afterPowerLoss, mode, 64).OpenTransactional())
    {
      using (var reader = recovered.BeginReadOnlyTransaction())
      {
        committed = 0;
        while (committed < count && reader.ContainsKey(2 * committed))
          ++committed;
        for (var i = 0; i < count; ++i)
        {
          // A transaction is recovered whole or not at all.
          Assert.That(reader.ContainsKey(2 * i), Is.EqualTo(i < committed));
          Assert.That(reader.ContainsKey(2 * i + 1), Is.EqualTo(i < committed));
        }
        Assert.That(reader.ContainsKey(-1), Is.False);
      }
      Assert.That(committed, Is.GreaterThan(count / 2));

      using var tx = recovered.BeginTransaction();
      tx.Upsert(-2, -2);
      tx.Commit();
    }
    using var reopened = CreateFactory(afterPowerLoss, mode, 64).OpenTransactional();
    using var check = reopened.BeginReadOnlyTransaction();
    Assert.That(check.ContainsKey(-2), Is.True);
    Assert.That(check.ContainsKey(2 * (committed - 1)), Is.True);
  }
}
