using System.Collections.Concurrent;
using ZoneTree.AbstractFileStream;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>
/// Tests iterators created while the tree keeps moving its mutable segment forward, merging it into the disk segment,
/// moving full disk segments to the bottom, and merging bottom segments. Iterators collect their segments without the
/// tree's locks, so segments move and retire while iterators are created. Every full scan must still see every key
/// exactly once, in order.
/// </summary>
public sealed class IteratorMergeConcurrencyTests
{
  [Test]
  public async Task IteratorsCreatedDuringMergesSeeEveryKeyExactlyOnce()
  {
    const int keyCount = 5_000;
    using var tree = new ZoneTreeFactory<int, int>(new InMemoryFileStreamProvider())
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDataDirectory("data/IteratorsCreatedDuringMerges")
        .SetMutableSegmentMaxItemCount(300)
        .SetDiskSegmentMaxItemCount(1_500)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.MinimumRecordCount = 50;
          options.MaximumRecordCount = 200;
        })
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.None)
        .OpenOrCreate();
    // A value of 0 would mark a key as deleted, so every value is positive.
    for (var key = 0; key < keyCount; ++key)
      tree.Upsert(key, 1);

    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var maintenance = tree.Maintenance;
    var merges = 0;
    var bottomMerges = 0;
    var writer = Task.Run(() =>
    {
      // Rewrites a share of the keys in every round, so each merge has new records to move.
      for (var round = 1; !stop.IsCancellationRequested; ++round)
      {
        for (var key = round % 7; key < keyCount; key += 7)
          tree.Upsert(key, round);
        maintenance.MoveMutableSegmentForward();
        maintenance.StartMergeOperation()?.Join();
        ++merges;
        var bottomCount = maintenance.BottomSegments.Count;
        if (bottomCount > 2)
        {
          maintenance.StartBottomSegmentsMergeOperation(0, bottomCount - 1)?.Join();
          ++bottomMerges;
        }
      }
    });

    var scans = 0;
    var failures = new ConcurrentQueue<string>();
    var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
      while (!stop.IsCancellationRequested)
      {
        using var iterator = tree.CreateIterator(IteratorType.NoRefresh);
        var expected = 0;
        while (iterator.Next())
        {
          if (iterator.CurrentKey != expected)
          {
            failures.Enqueue($"Expected key {expected}, but the scan returned {iterator.CurrentKey}.");
            break;
          }
          ++expected;
        }
        if (expected != keyCount)
          failures.Enqueue($"A scan saw {expected} of {keyCount} keys.");
        Interlocked.Increment(ref scans);
      }
    })).ToArray();
    await Task.WhenAll(readers.Append(writer));

    Assert.Multiple(() =>
    {
      Assert.That(failures, Is.Empty);
      Assert.That(scans, Is.GreaterThan(20));
      Assert.That(merges, Is.GreaterThan(5));
      Assert.That(bottomMerges, Is.Positive);
    });
  }
}
