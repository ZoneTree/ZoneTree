using ZoneTree.Collections.BTree;
using ZoneTree.Collections.BTree.Lock;
using ZoneTree.Comparers;

namespace ZoneTree.UnitTests;

public sealed class BTreeVisitTests
{
  const int LeafSize = 8;

  static BTree<int, int> CreateTree(BTreeLockMode lockMode = BTreeLockMode.NodeLevelMonitor)
  {
    return new BTree<int, int>(new Int32ComparerAscending(), lockMode,
        nodeSize: LeafSize, leafSize: LeafSize);
  }

  sealed class CollectedKeys
  {
    public List<int> Keys { get; } = new();
  }

  struct CollectingVisitor : ISegmentRecordVisitor<int, int>
  {
    public CollectedKeys Collected;

    public int StopAt;

    public int SkipSegmentAt;

    public SegmentVisitAction Visit(in int key, in int value)
    {
      Assert.That(value, Is.EqualTo(key + 1));
      Collected.Keys.Add(key);
      if (key == StopAt)
        return SegmentVisitAction.Stop;
      return key == SkipSegmentAt ? SegmentVisitAction.NextSegment : SegmentVisitAction.NextRecord;
    }
  }

  static (SegmentVisitAction Action, List<int> Keys) Visit(
      BTree<int, int> tree, int from, int stopAt = -1, int skipSegmentAt = -1)
  {
    var visitor = new CollectingVisitor
    {
      Collected = new CollectedKeys(),
      StopAt = stopAt,
      SkipSegmentAt = skipSegmentAt
    };
    var action = tree.Visit(in from, ref visitor);
    return (action, visitor.Collected.Keys);
  }

  [Test]
  public void VisitReadsTheSameRecordsAsTheIterator()
  {
    var tree = CreateTree();
    // Even keys, so every odd start key falls between records and leaves.
    for (var key = 0; key < 1000; key += 2)
      tree.Upsert(key, key + 1, out _);

    foreach (var from in Enumerable.Range(-1, 1003))
    {
      var iterator = new BTreeSeekableIterator<int, int>(tree);
      var expected = new List<int>();
      if (iterator.SeekToFirstGreaterOrEqualElement(in from))
      {
        do
          expected.Add(iterator.CurrentKey);
        while (iterator.Next());
      }
      var (action, keys) = Visit(tree, from);
      Assert.That(keys, Is.EqualTo(expected), $"from {from}");
      Assert.That(action, Is.EqualTo(SegmentVisitAction.NextSegment));
    }
  }

  [Test]
  public void VisitOfAnEmptyTreeVisitsNothing()
  {
    var (action, keys) = Visit(CreateTree(), 5);
    Assert.That(keys, Is.Empty);
    Assert.That(action, Is.EqualTo(SegmentVisitAction.NextSegment));
  }

  [Test]
  public void VisitEndsWithTheActionOfTheVisitor()
  {
    var tree = CreateTree();
    for (var key = 0; key < 100; ++key)
      tree.Upsert(key, key + 1, out _);

    var stopped = Visit(tree, 10, stopAt: 50);
    Assert.That(stopped.Action, Is.EqualTo(SegmentVisitAction.Stop));
    Assert.That(stopped.Keys, Is.EqualTo(Enumerable.Range(10, 41)));

    var skipped = Visit(tree, 10, skipSegmentAt: 50);
    Assert.That(skipped.Action, Is.EqualTo(SegmentVisitAction.NextSegment));
    Assert.That(skipped.Keys, Is.EqualTo(Enumerable.Range(10, 41)));
  }

  [Test]
  public void VisitWorksOnAReadOnlyTree()
  {
    var tree = CreateTree();
    for (var key = 0; key < 100; ++key)
      tree.Upsert(key, key + 1, out _);
    tree.SetTreeReadOnlyAndLockFree();

    Assert.That(Visit(tree, 10).Keys, Is.EqualTo(Enumerable.Range(10, 90)));
  }

  struct ThrowingVisitor : ISegmentRecordVisitor<int, int>
  {
    public SegmentVisitAction Visit(in int key, in int value)
    {
      throw new InvalidOperationException("The visitor failed.");
    }
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  [TestCase(BTreeLockMode.TopLevelMonitor)]
  [TestCase(BTreeLockMode.TopLevelReaderWriter)]
  public void FailingVisitorReleasesTheLocks(BTreeLockMode lockMode)
  {
    var tree = CreateTree(lockMode);
    for (var key = 0; key < 100; ++key)
      tree.Upsert(key, key + 1, out _);

    var from = 10;
    var visitor = new ThrowingVisitor();
    Assert.Throws<InvalidOperationException>(() => tree.Visit(in from, ref visitor));

    // Another thread, because monitors are reentrant.
    var write = Task.Run(() => tree.Upsert(10, 11, out _));
    Assert.That(write.Wait(TimeSpan.FromSeconds(30)), Is.True);
  }

  sealed class FoundValue
  {
    public int Value;
  }

  struct PointVisitor : ISegmentRecordVisitor<int, int>
  {
    public int Key;

    public FoundValue Found;

    public SegmentVisitAction Visit(in int key, in int value)
    {
      if (key == Key)
        Found.Value = value;
      return SegmentVisitAction.Stop;
    }
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  [TestCase(BTreeLockMode.TopLevelMonitor)]
  [TestCase(BTreeLockMode.TopLevelReaderWriter)]
  public async Task VisitFindsEveryPublishedKeyDuringSplits(BTreeLockMode lockMode)
  {
    const int count = 40_000;
    var tree = CreateTree(lockMode);
    // Keys are published in order, and each reader looks them up during the splits.
    var published = -1;
    var writer = Task.Run(() =>
    {
      for (var i = 0; i < count; ++i)
      {
        // Reversed bits spread the keys, so splits happen all over the tree.
        tree.Upsert(SpreadKey(i), i + 1, out _);
        Volatile.Write(ref published, i);
      }
    });
    var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
      var random = new Random(_);
      while (Volatile.Read(ref published) < count - 1)
      {
        var last = Volatile.Read(ref published);
        if (last < 0)
          continue;
        var i = random.Next(last + 1);
        var key = SpreadKey(i);
        var visitor = new PointVisitor { Key = key, Found = new FoundValue() };
        tree.Visit(in key, ref visitor);
        Assert.That(visitor.Found.Value, Is.EqualTo(i + 1), $"key {key}");
      }
    })).ToArray();

    await writer;
    await Task.WhenAll(readers);
    tree.Validate();
  }

  static int SpreadKey(int i)
  {
    var key = 0;
    for (var bit = 0; bit < 16; ++bit)
      key |= ((i >> bit) & 1) << (15 - bit);
    return key;
  }
}
