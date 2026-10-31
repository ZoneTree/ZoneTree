using System.Reflection;
using ZoneTree.Collections.BTree;
using ZoneTree.Collections.BTree.Lock;
using ZoneTree.Comparers;

namespace ZoneTree.UnitTests;

/// <summary>
/// Reads find their leaf without locking the inner nodes, while splits change them.
/// </summary>
public sealed class BTreeLockFreeSearchTests
{
  /// <summary>
  /// A key whose fields always hold the same value, so a torn read is detectable.
  /// </summary>
  readonly struct TwoFieldKey
  {
    public readonly long First;

    public readonly long Second;

    public TwoFieldKey(long value)
    {
      First = value;
      Second = value;
    }
  }

  sealed class TwoFieldKeyComparer : IRefComparer<TwoFieldKey>
  {
    public int Compare(in TwoFieldKey x, in TwoFieldKey y)
    {
      if (x.First != x.Second || y.First != y.Second)
        throw new InvalidOperationException("The comparer saw a torn key.");
      return x.First.CompareTo(y.First);
    }
  }

  struct FindVisitor : ISegmentRecordVisitor<int, int>
  {
    public bool IsVisited;

    public SegmentVisitAction Visit(in int key, in int value)
    {
      IsVisited = true;
      return SegmentVisitAction.Stop;
    }
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public void ReadsDoNotWaitForALockedRoot(BTreeLockMode lockMode)
  {
    var tree = new BTree<int, int>(new Int32ComparerAscending(), lockMode, nodeSize: 8, leafSize: 8);
    for (var key = 0; key < 1000; ++key)
      tree.Upsert(key, key + 1, out _);
    var root = (BTree<int, int>.Node)typeof(BTree<int, int>)
        .GetField("Root", BindingFlags.Instance | BindingFlags.NonPublic)
        .GetValue(tree);
    Assert.That(root, Is.Not.InstanceOf<BTree<int, int>.LeafNode>());

    root.WriteLock();
    try
    {
      var reads = Task.Run(() =>
      {
        var key = 500;
        Assert.That(tree.TryGetValue(in key, out var value), Is.True);
        Assert.That(value, Is.EqualTo(501));
        Assert.That(tree.ContainsKey(in key), Is.True);
        var visitor = new FindVisitor();
        tree.Visit(in key, ref visitor);
        Assert.That(visitor.IsVisited, Is.True);
        var iterator = new BTreeSeekableIterator<int, int>(tree);
        Assert.That(iterator.SeekToFirstGreaterOrEqualElement(in key), Is.True);
        Assert.That(iterator.CurrentKey, Is.EqualTo(500));
      });
      Assert.That(reads.Wait(TimeSpan.FromSeconds(10)), Is.True, "A read waits for the root.");
    }
    finally
    {
      root.WriteUnlock();
    }
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  [TestCase(BTreeLockMode.TopLevelMonitor)]
  [TestCase(BTreeLockMode.TopLevelReaderWriter)]
  public async Task ReadsFindEveryPublishedKeyDuringSplits(BTreeLockMode lockMode)
  {
    const int count = 60_000;
    // Small nodes split the inner nodes often. Moving their keys is what can tear a key.
    var tree = new BTree<TwoFieldKey, int>(new TwoFieldKeyComparer(), lockMode, nodeSize: 4, leafSize: 4);
    var published = -1;
    var writer = Task.Run(() =>
    {
      for (var i = 0; i < count; ++i)
      {
        var key = new TwoFieldKey(SpreadKey(i));
        tree.Upsert(in key, i + 1, out _);
        Volatile.Write(ref published, i);
      }
    });
    var readers = Enumerable.Range(0, 4).Select(seed => Task.Run(() =>
    {
      var random = new Random(seed);
      while (Volatile.Read(ref published) < count - 1)
      {
        var last = Volatile.Read(ref published);
        if (last < 0)
          continue;
        var i = random.Next(last + 1);
        var key = new TwoFieldKey(SpreadKey(i));
        Assert.That(tree.TryGetValue(in key, out var value), Is.True, $"key {key.First}");
        Assert.That(value, Is.EqualTo(i + 1));
        Assert.That(tree.ContainsKey(in key), Is.True, $"key {key.First}");
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
