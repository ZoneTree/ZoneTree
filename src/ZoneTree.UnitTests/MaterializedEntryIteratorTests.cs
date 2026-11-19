using ZoneTree.AbstractFileStream;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>
/// Tests prefetching disk segment iterators over blocks whose entries are only partly materialized. Short scans with
/// varying prefetch sizes leave scattered runs of materialized entries, so later reads mix entries copied from the
/// cache with entries read from the block.
/// </summary>
public sealed class MaterializedEntryIteratorTests
{
  const int RecordCount = 3000;

  // Iterators prefetch only when asked to, and only prefetching iterators materialize entries.
  const int PrefetchSize = 16;

  [Test]
  public void ScansAndSeeksSeeEveryEntryOverPartlyMaterializedBlocks(
      [Values] DiskSegmentMode mode,
      [Values] bool contributeToTheBlockCache)
  {
    // One case for each disk segment layout.
    AssertScansAndSeeks(mode, contributeToTheBlockCache, i => i.ToString("D6"), i => (long)i);
    AssertScansAndSeeks(mode, contributeToTheBlockCache, i => i.ToString("D6"), i => $"value-{i}");
    AssertScansAndSeeks(mode, contributeToTheBlockCache, i => i, i => $"value-{i}");
    AssertScansAndSeeks(mode, contributeToTheBlockCache, i => i, i => (long)i);
  }

  static void AssertScansAndSeeks<TKey, TValue>(
      DiskSegmentMode mode,
      bool contributeToTheBlockCache,
      Func<int, TKey> keyOf,
      Func<int, TValue> valueOf)
  {
    // Small blocks and parts put chunks across block and part edges.
    using var tree = CreateTree(mode, keyOf, valueOf, 4096);
    var random = new Random(7);
    AssertSeeks(tree, keyOf, random, contributeToTheBlockCache);
    for (var i = 0; i < 300; ++i)
    {
      using var iterator = tree.CreateIterator(
          IteratorType.NoRefresh,
          includeDeletedRecords: false,
          contributeToTheBlockCache,
          diskSegmentPrefetchSize: random.Next(2, 20));
      iterator.Seek(keyOf(random.Next(-1, RecordCount * 2 + 2)));
      for (var step = random.Next(1, 4); step > 0 && iterator.Next(); --step)
      {
      }
    }
    AssertSeeks(tree, keyOf, random, contributeToTheBlockCache);
    AssertForwardAndReverseScans(tree, keyOf, valueOf, contributeToTheBlockCache);
    AssertNextAndPrev(tree, keyOf, valueOf, random, contributeToTheBlockCache);
    // The scans above leave every entry materialized when blocks stay in the block cache.
    AssertSeeks(tree, keyOf, random, contributeToTheBlockCache);
    AssertForwardAndReverseScans(tree, keyOf, valueOf, contributeToTheBlockCache);
  }

  static IZoneTree<TKey, TValue> CreateTree<TKey, TValue>(
      DiskSegmentMode mode,
      Func<int, TKey> keyOf,
      Func<int, TValue> valueOf,
      int compressionBlockSize)
  {
    var tree = new ZoneTreeFactory<TKey, TValue>(new InMemoryFileStreamProvider())
        .SetDataDirectory("materialized-entry-iterators")
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .ConfigureWriteAheadLogOptions(options => options.Mode = WriteAheadLogMode.None)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.Mode = mode;
          options.MinimumRecordCount = 500;
          options.MaximumRecordCount = 700;
          options.CompressionBlockSize = compressionBlockSize;
          options.DefaultSparseArrayStepSize = 64;
          options.KeyCacheSize = 0;
          options.ValueCacheSize = 0;
        })
        .OpenOrCreate();
    // Even keys only, so seeks also look for missing keys.
    for (var i = 0; i < RecordCount; ++i)
      tree.Upsert(keyOf(i * 2), valueOf(i * 2));
    tree.Maintenance.MoveMutableSegmentForward();
    tree.Maintenance.StartMergeOperation().Join();
    return tree;
  }

  static void AssertForwardAndReverseScans<TKey, TValue>(
      IZoneTree<TKey, TValue> tree,
      Func<int, TKey> keyOf,
      Func<int, TValue> valueOf,
      bool contributeToTheBlockCache)
  {
    var expected = Enumerable.Range(0, RecordCount)
        .Select(i => (keyOf(i * 2), valueOf(i * 2)))
        .ToList();
    using (var iterator = tree.CreateIterator(IteratorType.NoRefresh, false, contributeToTheBlockCache, PrefetchSize))
    {
      var forward = new List<(TKey, TValue)>();
      while (iterator.Next())
        forward.Add((iterator.CurrentKey, iterator.CurrentValue));
      Assert.That(forward, Is.EqualTo(expected), "forward scan");
    }
    using (var iterator = tree.CreateReverseIterator(IteratorType.NoRefresh, false, contributeToTheBlockCache, PrefetchSize))
    {
      var backward = new List<(TKey, TValue)>();
      while (iterator.Next())
        backward.Add((iterator.CurrentKey, iterator.CurrentValue));
      backward.Reverse();
      Assert.That(backward, Is.EqualTo(expected), "reverse scan");
    }
  }

  static void AssertSeeks<TKey, TValue>(
      IZoneTree<TKey, TValue> tree,
      Func<int, TKey> keyOf,
      Random random,
      bool contributeToTheBlockCache)
  {
    for (var i = 0; i < 200; ++i)
    {
      var probe = random.Next(-1, RecordCount * 2 + 2);
      using (var iterator = tree.CreateIterator(IteratorType.NoRefresh, false, contributeToTheBlockCache, PrefetchSize))
      {
        iterator.Seek(keyOf(probe));
        var first = probe <= 0 ? 0 : (probe + 1) / 2 * 2;
        if (first < RecordCount * 2)
        {
          Assert.That(iterator.Next(), Is.True, $"forward seek {probe}");
          Assert.That(iterator.CurrentKey, Is.EqualTo(keyOf(first)), $"forward seek {probe}");
        }
        else
        {
          Assert.That(iterator.Next(), Is.False, $"forward seek {probe}");
        }
      }
      using (var iterator = tree.CreateReverseIterator(IteratorType.NoRefresh, false, contributeToTheBlockCache, PrefetchSize))
      {
        iterator.Seek(keyOf(probe));
        var last = Math.Min(probe / 2 * 2, RecordCount * 2 - 2);
        if (probe >= 0)
        {
          Assert.That(iterator.Next(), Is.True, $"reverse seek {probe}");
          Assert.That(iterator.CurrentKey, Is.EqualTo(keyOf(last)), $"reverse seek {probe}");
        }
        else
        {
          Assert.That(iterator.Next(), Is.False, $"reverse seek {probe}");
        }
      }
    }
  }

  static void AssertNextAndPrev<TKey, TValue>(
      IZoneTree<TKey, TValue> tree,
      Func<int, TKey> keyOf,
      Func<int, TValue> valueOf,
      Random random,
      bool contributeToTheBlockCache)
  {
    // Turning around inside a block reads entries that the same iterator materialized moments before.
    var iterator = tree.Maintenance.DiskSegment.GetSeekableIterator(contributeToTheBlockCache, PrefetchSize);
    Assert.That(iterator.SeekBegin(), Is.True);
    var position = 0;
    for (var i = 0; i < 400; ++i)
    {
      var steps = random.Next(1, 40);
      var forward = random.Next(3) != 0;
      for (var step = 0; step < steps; ++step)
      {
        if (forward ? !iterator.Next() : !iterator.Prev())
          break;
        position += forward ? 1 : -1;
      }
      Assert.That(iterator.CurrentKey, Is.EqualTo(keyOf(position * 2)), $"key at {position}");
      Assert.That(iterator.CurrentValue, Is.EqualTo(valueOf(position * 2)), $"value at {position}");
    }
  }
}
