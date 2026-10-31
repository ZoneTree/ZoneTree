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
  public void ReadsDoNotWaitForALockedLeaf(BTreeLockMode lockMode)
  {
    var tree = new BTree<int, int>(new Int32ComparerAscending(), lockMode, nodeSize: 8, leafSize: 8);
    for (var key = 0; key < 1000; ++key)
      tree.Upsert(key, key + 1, out _);
    var leaf = (BTree<int, int>.LeafNode)typeof(BTree<int, int>)
        .GetField("FirstLeafNode", BindingFlags.Instance | BindingFlags.NonPublic)
        .GetValue(tree);

    leaf.WriteLock();
    try
    {
      var reads = Task.Run(() =>
      {
        var key = leaf.Keys[0];
        Assert.That(tree.TryGetValue(in key, out var value), Is.True);
        Assert.That(value, Is.EqualTo(key + 1));
        Assert.That(tree.ContainsKey(in key), Is.True);
        var visitor = new FindVisitor();
        tree.Visit(in key, ref visitor);
        Assert.That(visitor.IsVisited, Is.True);
      });
      Assert.That(reads.Wait(TimeSpan.FromSeconds(10)), Is.True, "A read waits for the leaf.");
    }
    finally
    {
      leaf.WriteUnlock();
    }
  }

  struct WritingVisitor : ISegmentRecordVisitor<int, int>
  {
    public BTree<int, int> Tree;

    public SegmentVisitAction Visit(in int key, in int value)
    {
      Assert.That(Tree.TryGetValue(in key, out var current), Is.True);
      Assert.That(current, Is.EqualTo(value));
      // The same leaf that the visit read this record from.
      Tree.Upsert(key, value, out _);
      return key < 100 ? SegmentVisitAction.NextRecord : SegmentVisitAction.Stop;
    }
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  [TestCase(BTreeLockMode.TopLevelMonitor)]
  [TestCase(BTreeLockMode.TopLevelReaderWriter)]
  public void VisitorRunsOutsideTheLocks(BTreeLockMode lockMode)
  {
    var tree = new BTree<int, int>(new Int32ComparerAscending(), lockMode, nodeSize: 8, leafSize: 8);
    for (var key = 0; key < 200; ++key)
      tree.Upsert(key, key + 1, out _);

    var start = 0;
    var visitor = new WritingVisitor { Tree = tree };
    var visit = Task.Run(() => tree.Visit(in start, ref visitor));

    Assert.That(visit.Wait(TimeSpan.FromSeconds(30)), Is.True);
    Assert.That(visit.Result, Is.EqualTo(SegmentVisitAction.Stop));
    tree.Validate();
  }

  /// <summary>
  /// A value whose fields always hold the same value, so a torn read is detectable.
  /// </summary>
  readonly struct TwoFieldValue
  {
    public readonly long First;

    public readonly long Second;

    public TwoFieldValue(long value)
    {
      First = value;
      Second = value;
    }

    public bool IsTorn => First != Second;
  }

  struct TornValueVisitor : ISegmentRecordVisitor<int, TwoFieldValue>
  {
    public SegmentVisitAction Visit(in int key, in TwoFieldValue value)
    {
      Assert.That(value.IsTorn, Is.False);
      return SegmentVisitAction.Stop;
    }
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public async Task ReadsNeverSeeATornValue(BTreeLockMode lockMode)
  {
    const int keyCount = 16;
    var tree = new BTree<int, TwoFieldValue>(new Int32ComparerAscending(), lockMode);
    for (var key = 0; key < keyCount; ++key)
      tree.Upsert(key, new TwoFieldValue(0), out _);

    using var stop = new CancellationTokenSource();
    // Dedicated threads, because on a small machine these busy loops
    // fill the thread pool and the other readers would start too late.
    var writer = Task.Factory.StartNew(() =>
    {
      for (var i = 1L; !stop.IsCancellationRequested; ++i)
      {
        var value = new TwoFieldValue(i);
        var key = (int)(i % keyCount);
        tree.Upsert(key, in value, out _);
        tree.AddOrUpdate(
            key,
            (ref TwoFieldValue x) => x = value,
            (ref TwoFieldValue x) => x = value,
            out _);
      }
    }, TaskCreationOptions.LongRunning);
    var readers = Enumerable.Range(0, 4).Select(seed => Task.Factory.StartNew(() =>
    {
      var random = new Random(seed);
      var reads = 0L;
      while (!stop.IsCancellationRequested)
      {
        var key = random.Next(keyCount);
        Assert.That(tree.TryGetValue(in key, out var value), Is.True);
        Assert.That(value.IsTorn, Is.False);
        var visitor = new TornValueVisitor();
        tree.Visit(in key, ref visitor);
        ++reads;
      }
      return reads;
    }, TaskCreationOptions.LongRunning)).ToArray();

    await Task.Delay(1000);
    stop.Cancel();
    await writer;
    var readCounts = await Task.WhenAll(readers);
    Assert.That(readCounts, Has.All.GreaterThan(0));
  }

  [Test]
  public void FailingUpdaterLeavesTheValueAndTheReadsIntact()
  {
    var tree = new BTree<int, TwoFieldValue>(new Int32ComparerAscending(), BTreeLockMode.NodeLevelMonitor);
    tree.Upsert(1, new TwoFieldValue(10), out _);

    Assert.Throws<InvalidOperationException>(() => tree.AddOrUpdate(
        1,
        (ref TwoFieldValue x) => { },
        (ref TwoFieldValue x) =>
        {
          x = new TwoFieldValue(20);
          throw new InvalidOperationException("The updater failed.");
        },
        out _));

    var key = 1;
    var read = Task.Run(() =>
    {
      Assert.That(tree.TryGetValue(in key, out var value), Is.True);
      return value;
    });
    Assert.That(read.Wait(TimeSpan.FromSeconds(10)), Is.True);
    Assert.That(read.Result.First, Is.EqualTo(10));
  }

  /// <summary>
  /// Runs an action during a comparison of the reader thread, which
  /// is how a read meets a concurrent write at an exact moment.
  /// </summary>
  sealed class InterruptingComparer : IRefComparer<int>
  {
    public Func<bool> ShouldInterrupt { get; set; }

    public Action Interrupt { get; set; }

    public int Compare(in int x, in int y)
    {
      if (Volatile.Read(ref readerThreadId) == Environment.CurrentManagedThreadId && ShouldInterrupt())
        Interrupt();
      return x.CompareTo(y);
    }

    int readerThreadId;

    public void SetReaderThread() => Volatile.Write(ref readerThreadId, Environment.CurrentManagedThreadId);
  }

  static void ThrowAfter(Action write)
  {
    // A dedicated thread, as a concurrent writer is:
    // waiting for a task could run it inline on this thread.
    var writer = new Thread(() => write());
    writer.Start();
    writer.Join();
    throw new InvalidOperationException("A torn key.");
  }

  [Test]
  public void ComparerFailureOnALeafThatChangedIsRetried()
  {
    var comparer = new InterruptingComparer();
    var tree = new BTree<int, int>(comparer, BTreeLockMode.NodeLevelMonitor);
    tree.Upsert(1, 10, out _);

    var interruptions = 0;
    comparer.ShouldInterrupt = () => ++interruptions == 1;
    comparer.Interrupt = () => ThrowAfter(() => tree.Upsert(1, 20, out _));
    comparer.SetReaderThread();

    var key = 1;
    Assert.That(tree.TryGetValue(in key, out var value), Is.True);
    Assert.That(value, Is.EqualTo(20));
    Assert.That(interruptions, Is.GreaterThan(1));
  }

  [Test]
  public void ComparerFailureOnAnInnerNodeThatChangedIsRetried()
  {
    var comparer = new InterruptingComparer();
    var tree = new BTree<int, int>(comparer, BTreeLockMode.NodeLevelMonitor, nodeSize: 4, leafSize: 4);
    for (var i = 0; i < 100; ++i)
      tree.Upsert(i * 2, i * 2 + 1, out _);

    var interruptions = 0;
    comparer.ShouldInterrupt = () => ++interruptions == 1;
    // These inserts split inner nodes all over the tree, including the root.
    comparer.Interrupt = () => ThrowAfter(() =>
    {
      for (var i = 0; i < 1000; ++i)
        tree.Upsert(i * 2 + 1, i * 2 + 2, out _);
    });
    comparer.SetReaderThread();

    var key = 50;
    Assert.That(tree.TryGetValue(in key, out var value), Is.True);
    Assert.That(value, Is.EqualTo(51));
    Assert.That(interruptions, Is.GreaterThan(1));
    tree.Validate();
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public void ReadTakesTheLockWhenTheLeafKeepsChanging(BTreeLockMode lockMode)
  {
    var comparer = new InterruptingComparer();
    var tree = new BTree<int, int>(comparer, lockMode);
    tree.Upsert(1, 0, out _);
    var leaf = (BTree<int, int>.LeafNode)typeof(BTree<int, int>)
        .GetField("FirstLeafNode", BindingFlags.Instance | BindingFlags.NonPublic)
        .GetValue(tree);

    var writes = 0;
    var isReaderHoldingTheLock = false;
    comparer.ShouldInterrupt = () => !isReaderHoldingTheLock;
    // Every read fails until the reader locks the leaf, which only then keeps the writer out.
    comparer.Interrupt = () =>
    {
      var writer = new Thread(() =>
      {
        if (!leaf.TryEnterWriteLock(0))
        {
          isReaderHoldingTheLock = true;
          return;
        }
        leaf.WriteUnlock();
        tree.Upsert(1, ++writes, out _);
      });
      writer.Start();
      writer.Join();
      if (!isReaderHoldingTheLock)
        throw new InvalidOperationException("A torn key.");
    };

    var key = 1;
    var read = Task.Factory.StartNew(() =>
    {
      comparer.SetReaderThread();
      Assert.That(tree.TryGetValue(in key, out var value), Is.True);
      return value;
    }, TaskCreationOptions.LongRunning);

    Assert.That(read.Wait(TimeSpan.FromSeconds(30)), Is.True, "The read keeps retrying.");
    Assert.That(isReaderHoldingTheLock, Is.True);
    Assert.That(read.Result, Is.EqualTo(writes));
  }

  [Test]
  public void ComparerFailureOnAnUnchangedLeafReachesTheCaller()
  {
    var comparer = new InterruptingComparer();
    var tree = new BTree<int, int>(comparer, BTreeLockMode.NodeLevelMonitor);
    tree.Upsert(1, 10, out _);

    comparer.ShouldInterrupt = () => true;
    comparer.Interrupt = () => throw new InvalidOperationException("A real failure.");
    comparer.SetReaderThread();

    var key = 1;
    Assert.Throws<InvalidOperationException>(() => tree.TryGetValue(in key, out _));
    Assert.Throws<InvalidOperationException>(() => tree.ContainsKey(in key));
    var visitor = new FindIntVisitor();
    Assert.Throws<InvalidOperationException>(() => tree.Visit(in key, ref visitor));
  }

  struct FindIntVisitor : ISegmentRecordVisitor<int, int>
  {
    public SegmentVisitAction Visit(in int key, in int value) => SegmentVisitAction.Stop;
  }

  sealed class VisitedKeys
  {
    public List<int> Keys { get; } = new();
  }

  struct AllKeysVisitor : ISegmentRecordVisitor<int, int>
  {
    public VisitedKeys Visited;

    public SegmentVisitAction Visit(in int key, in int value)
    {
      Assert.That(value, Is.EqualTo(key + 1));
      Visited.Keys.Add(key);
      return SegmentVisitAction.NextRecord;
    }
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public async Task VisitDuringInsertsIsOrderedAndKeepsEveryExistingKey(BTreeLockMode lockMode)
  {
    const int keyCount = 20_000;
    var tree = new BTree<int, int>(new Int32ComparerAscending(), lockMode, nodeSize: 8, leafSize: 8);
    // The even keys stay in the tree, and writers insert the odd keys between them.
    for (var key = 0; key < keyCount; key += 2)
      tree.Upsert(key, key + 1, out _);

    var writer = Task.Run(() =>
    {
      for (var i = 0; i < keyCount / 2; ++i)
      {
        var key = (i * 7919 % (keyCount / 2)) * 2 + 1;
        tree.Upsert(key, key + 1, out _);
      }
    });
    var visitCount = 0;
    while (!writer.IsCompleted || visitCount == 0)
    {
      var start = 0;
      var visitor = new AllKeysVisitor { Visited = new VisitedKeys() };
      tree.Visit(in start, ref visitor);
      var keys = visitor.Visited.Keys;
      for (var i = 1; i < keys.Count; ++i)
        Assert.That(keys[i], Is.GreaterThan(keys[i - 1]));
      Assert.That(keys.Where(x => x % 2 == 0), Is.EqualTo(Enumerable.Range(0, keyCount / 2).Select(x => x * 2)));
      ++visitCount;
    }
    await writer;
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public async Task ReadsOfTheNewestKeysSeeEveryPublishedWrite(BTreeLockMode lockMode)
  {
    const int count = 300_000;
    var tree = new BTree<TwoFieldKey, int>(new TwoFieldKeyComparer(), lockMode);
    var published = -1;
    // Readers that start after the writer finished would check nothing.
    using var start = new Barrier(5);
    // All writes go to the rightmost leaf, and so do the reads.
    var writer = Task.Factory.StartNew(() =>
    {
      start.SignalAndWait();
      for (var i = 0; i < count; ++i)
      {
        var key = new TwoFieldKey(i);
        tree.Upsert(in key, i + 1, out _);
        Volatile.Write(ref published, i);
      }
    }, TaskCreationOptions.LongRunning);
    var readers = Enumerable.Range(0, 4).Select(seed => Task.Factory.StartNew(() =>
    {
      start.SignalAndWait();
      var random = new Random(seed);
      while (Volatile.Read(ref published) < count - 1)
      {
        var last = Volatile.Read(ref published);
        if (last < 0)
          continue;
        var i = Math.Max(0, last - random.Next(32));
        var key = new TwoFieldKey(i);
        Assert.That(tree.TryGetValue(in key, out var value), Is.True, $"key {i}");
        Assert.That(value, Is.EqualTo(i + 1));
      }
    }, TaskCreationOptions.LongRunning)).ToArray();

    await writer;
    await Task.WhenAll(readers);
    tree.Validate();
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
    // Readers that start after the writer finished would check nothing.
    using var start = new Barrier(5);
    var writer = Task.Factory.StartNew(() =>
    {
      start.SignalAndWait();
      for (var i = 0; i < count; ++i)
      {
        var key = new TwoFieldKey(SpreadKey(i));
        tree.Upsert(in key, i + 1, out _);
        Volatile.Write(ref published, i);
      }
    }, TaskCreationOptions.LongRunning);
    var readers = Enumerable.Range(0, 4).Select(seed => Task.Factory.StartNew(() =>
    {
      start.SignalAndWait();
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
    }, TaskCreationOptions.LongRunning)).ToArray();

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
