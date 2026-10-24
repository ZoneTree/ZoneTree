using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class DiskSegmentSearchTests
{
  [Test]
  public void ConsecutiveIntegerKeysRespectBoundsAndGaps(
      [Values(long.MinValue, -16L, 0L, long.MaxValue - 31)] long first,
      [Values] bool descending,
      [Values] bool gap)
  {
    AssertIntegerRange(first, descending, gap, new Int64ComparerAscending(), new Int64ComparerDescending(),
        key => key, key => $"value-{key}");
    AssertIntegerRange(first, descending, gap, new Int64ComparerAscending(), new Int64ComparerDescending(),
        key => key, key => key);
    var firstInt = first < int.MinValue ? int.MinValue : first > int.MaxValue - 31 ? int.MaxValue - 31 : first;
    AssertIntegerRange(firstInt, descending, gap, new Int32ComparerAscending(), new Int32ComparerDescending(),
        key => (int)key, key => key);
  }

  static void AssertIntegerRange<TKey, TValue>(
      long first, bool descending, bool gap,
      IRefComparer<TKey> ascendingComparer, IRefComparer<TKey> descendingComparer,
      Func<long, TKey> keyFactory, Func<long, TValue> valueFactory)
  {
    var comparer = descending ? descendingComparer : ascendingComparer;
    var provider = new InMemoryFileStreamProvider();
    ZoneTreeFactory<TKey, TValue> CreateFactory() => new ZoneTreeFactory<TKey, TValue>(provider)
        .SetDataDirectory("integer-search-tests")
        .SetComparer(comparer)
        .DisableDeletion()
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.None)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DiskSegmentMode = DiskSegmentMode.SingleDiskSegment;
          options.DefaultSparseArrayStepSize = 16;
          options.SearchHintPrefetchSize = 0;
        });
    var numbers = Enumerable.Range(0, 32).Where(i => !gap || i != 16).Select(i => first + i).ToArray();
    using (var tree = CreateFactory().OpenOrCreate())
    {
      foreach (var key in numbers) tree.Upsert(keyFactory(key), valueFactory(key));
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
    }
    // Exercise persisted sparse metadata as well as comparisons near signed
    // integer overflow. A gap must disable direct positioning.
    using var reopened = CreateFactory().OpenOrCreate();
    var keys = numbers.Select(keyFactory).ToList();
    keys.Sort((left, right) => comparer.Compare(in left, in right));
    var probes = Enumerable.Range(0, 32).Select(i => first + i).Concat(new[] { first, first + 31 });
    if (typeof(TKey) == typeof(long)) probes = probes.Concat(new[] { long.MinValue, long.MaxValue });
    else probes = probes.Concat(new[] { (long)int.MinValue, int.MaxValue });
    foreach (var probe in probes)
    {
      var key = keyFactory(probe);
      var found = numbers.Contains(probe);
      Assert.That(reopened.TryGet(key, out var value), Is.EqualTo(found));
      Assert.That(reopened.ContainsKey(key), Is.EqualTo(found));
      if (found) Assert.That(value, Is.EqualTo(valueFactory(probe)));
      var lower = keys.FindIndex(candidate => comparer.Compare(in candidate, in key) >= 0);
      var upper = keys.FindLastIndex(candidate => comparer.Compare(in candidate, in key) <= 0);
      if (lower < 0) lower = keys.Count;
      Assert.That(reopened.Maintenance.DiskSegment.GetFirstGreaterOrEqualPosition(in key), Is.EqualTo(lower));
      Assert.That(reopened.Maintenance.DiskSegment.GetLastSmallerOrEqualPosition(in key), Is.EqualTo(upper));
    }
  }

  [Test]
  public void SearchesAndSeeksAcrossAllLayouts(
      [Values(0, 1, 16)] int sparseStep,
      [Values] bool descending,
      [Values(128, 4096)] int blockSize)
  {
    IRefComparer<int> integerComparer = descending
        ? new Int32ComparerDescending() : new Int32ComparerAscending();
    IRefComparer<string> stringComparer = descending
        ? new StringOrdinalComparerDescending() : new StringOrdinalComparerAscending();
    AssertSearches(sparseStep, integerComparer, i => i, i => (long)i, blockSize: blockSize);
    AssertSearches(sparseStep, integerComparer, i => i, i => $"value-{i}", blockSize: blockSize);
    AssertSearches(sparseStep, stringComparer, i => i.ToString("D6"), i => (long)i, blockSize: blockSize);
    AssertSearches(sparseStep, stringComparer, i => i.ToString("D6"), i => $"value-{i}", blockSize: blockSize);
  }

  [Test]
  public void SearchesVariableKeysSeparatedByLargeValues()
  {
    AssertSearches(16, new StringOrdinalComparerAscending(),
        i => i.ToString("D6"), i => new string('x', 70_000) + i,
        recordCount: 40, blockSize: 4096);
  }

  static void AssertSearches<TKey, TValue>(
      int sparseStep,
      IRefComparer<TKey> comparer,
      Func<int, TKey> keyFactory,
      Func<int, TValue> valueFactory,
      int recordCount = 3000,
      int blockSize = 128)
  {
    using var tree = new ZoneTreeFactory<TKey, TValue>(new InMemoryFileStreamProvider())
        .SetDataDirectory("search-tests")
        .SetComparer(comparer)
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.None)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DiskSegmentMode = DiskSegmentMode.SingleDiskSegment;
          options.DefaultSparseArrayStepSize = sparseStep;
          options.CompressionBlockSize = blockSize;
          options.KeyCacheSize = 0;
          options.ValueCacheSize = 0;
        })
        .OpenOrCreate();
    var keys = Enumerable.Range(1, recordCount).Select(i => keyFactory(i * 2)).ToList();
    keys.Sort((left, right) => comparer.Compare(in left, in right));
    for (var i = 1; i <= recordCount; i++)
      tree.Upsert(keyFactory(i * 2), valueFactory(i * 2));
    tree.Maintenance.MoveMutableSegmentForward();
    tree.Maintenance.StartMergeOperation().Join();

    var segment = tree.Maintenance.DiskSegment;
    // Include both endpoints, sparse-array hits, gaps, and keys outside the
    // segment. Tiny compression blocks force search windows across blocks.
    var probes = new Random(123).GetItems(
        Enumerable.Range(0, recordCount * 2 + 3).ToArray(), 100);
    foreach (var probe in probes.Concat(new[] { 0, 1, 2, recordCount * 2, recordCount * 2 + 1 }))
    {
      var key = keyFactory(probe);
      var found = probe >= 2 && probe <= recordCount * 2 && probe % 2 == 0;
      Assert.That(tree.TryGet(key, out var value), Is.EqualTo(found), $"lookup {probe}");
      Assert.That(tree.ContainsKey(key), Is.EqualTo(found), $"contains {probe}");
      if (found)
        Assert.That(value, Is.EqualTo(valueFactory(probe)));
      var first = keys.FindIndex(candidate => comparer.Compare(in candidate, in key) >= 0);
      if (first < 0) first = keys.Count;
      var last = keys.FindLastIndex(candidate => comparer.Compare(in candidate, in key) <= 0);
      Assert.That(segment.GetFirstGreaterOrEqualPosition(in key), Is.EqualTo(first), $"forward {probe}");
      Assert.That(segment.GetLastSmallerOrEqualPosition(in key), Is.EqualTo(last), $"reverse {probe}");
    }
  }
}
