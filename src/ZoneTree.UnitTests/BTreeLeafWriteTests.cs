using System.Collections.Concurrent;
using System.Reflection;
using ZoneTree.Collections.BTree;
using ZoneTree.Collections.BTree.Lock;
using ZoneTree.Comparers;

namespace ZoneTree.UnitTests;

/// <summary>
/// Writes find their leaf without locking the inner nodes and lock only the
/// leaf, unless the leaf is full and has to split.
/// </summary>
public sealed class BTreeLeafWriteTests
{
  static BTree<int, int> CreateTree(BTreeLockMode lockMode, int size = 8)
  {
    return new BTree<int, int>(new Int32ComparerAscending(), lockMode, nodeSize: size, leafSize: size);
  }

  static BTree<int, int>.Node GetRoot(BTree<int, int> tree)
  {
    return (BTree<int, int>.Node)typeof(BTree<int, int>)
        .GetField("Root", BindingFlags.Instance | BindingFlags.NonPublic)
        .GetValue(tree);
  }

  static BTree<int, int>.LeafNode GetFirstLeaf(BTree<int, int> tree)
  {
    return (BTree<int, int>.LeafNode)typeof(BTree<int, int>)
        .GetField("FirstLeafNode", BindingFlags.Instance | BindingFlags.NonPublic)
        .GetValue(tree);
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public void WritesToALeafWithRoomDoNotWaitForALockedRoot(BTreeLockMode lockMode)
  {
    var tree = CreateTree(lockMode);
    // Ascending inserts leave the first leaf half full after its split.
    for (var key = 0; key < 1000; ++key)
      tree.Upsert(key * 10, key, out _);
    var root = GetRoot(tree);
    Assert.That(root, Is.Not.InstanceOf<BTree<int, int>.LeafNode>());

    root.WriteLock();
    try
    {
      var writes = Task.Factory.StartNew(() =>
      {
        Assert.That(tree.Upsert(1, 1, out _), Is.True);
        Assert.That(tree.TryInsert(2, 2, out _), Is.True);
        tree.AddOrUpdate(
            3,
            (ref int value) => value = 3,
            (ref int value) => value = -1,
            out _);
      }, TaskCreationOptions.LongRunning);
      Assert.That(writes.Wait(TimeSpan.FromSeconds(10)), Is.True, "A write waits for the root.");
    }
    finally
    {
      root.WriteUnlock();
    }
    foreach (var key in new[] { 1, 2, 3 })
    {
      Assert.That(tree.TryGetValue(in key, out var value), Is.True);
      Assert.That(value, Is.EqualTo(key));
    }
    tree.Validate();
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public void WriteToAFullLeafGoesThroughTheRoot(BTreeLockMode lockMode)
  {
    var tree = CreateTree(lockMode);
    for (var key = 0; key < 1000; ++key)
      tree.Upsert(key * 10, key, out _);
    // Fill the first leaf.
    var leaf = GetFirstLeaf(tree);
    for (var key = 1; !leaf.IsFull; ++key)
      tree.Upsert(key, key, out _);
    var root = GetRoot(tree);
    var newKey = leaf.Keys[0] + 5;
    var existing = tree.ContainsKey(in newKey);
    Assert.That(existing, Is.False);

    Task write;
    root.WriteLock();
    try
    {
      write = Task.Factory.StartNew(
          () => tree.Upsert(newKey, 7, out _), TaskCreationOptions.LongRunning);
      Assert.That(write.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "The split needs the path from the root.");
    }
    finally
    {
      root.WriteUnlock();
    }
    Assert.That(write.Wait(TimeSpan.FromSeconds(30)), Is.True);
    Assert.That(tree.TryGetValue(in newKey, out var value), Is.True);
    Assert.That(value, Is.EqualTo(7));
    tree.Validate();
    tree.ValidateLeafs();
  }

  [Test]
  public void WriterWaitingForALeafThatSplitsWritesToTheNewLeaf()
  {
    // A monitor is reentrant, so this thread can split the leaf it holds.
    var tree = CreateTree(BTreeLockMode.NodeLevelMonitor);
    for (var key = 0; key < 100; ++key)
      tree.Upsert(key * 100, key, out _);
    var leaf = GetFirstLeaf(tree);
    const int waitingKey = 50;

    // A writer that holds the path from the root while it waits for
    // the leaf would block the split forever, so the scenario has a time limit.
    Task waitingWriter = null;
    var scenario = Task.Factory.StartNew(() =>
    {
      leaf.WriteLock();
      try
      {
        waitingWriter = Task.Factory.StartNew(
            () => tree.Upsert(waitingKey, 1, out _), TaskCreationOptions.LongRunning);
        // Give the writer the time to find the leaf and to wait for its lock.
        Assert.That(waitingWriter.Wait(TimeSpan.FromMilliseconds(200)), Is.False);
        for (var key = 1; key <= 8; ++key)
          tree.Upsert(key * 2 - 1, key, out _);
      }
      finally
      {
        leaf.WriteUnlock();
      }
    }, TaskCreationOptions.LongRunning);

    Assert.That(scenario.Wait(TimeSpan.FromSeconds(30)), Is.True, "The waiting writer blocks the split.");
    Assert.That(waitingWriter.Wait(TimeSpan.FromSeconds(30)), Is.True);
    var searched = waitingKey;
    Assert.That(tree.TryGetValue(in searched, out var value), Is.True, "The write went to the replaced leaf.");
    Assert.That(value, Is.EqualTo(1));
    tree.Validate();
    tree.ValidateLeafs();
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor, 4)]
  [TestCase(BTreeLockMode.NodeLevelMonitor, 128)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter, 4)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter, 128)]
  [TestCase(BTreeLockMode.TopLevelMonitor, 4)]
  [TestCase(BTreeLockMode.TopLevelReaderWriter, 4)]
  public void ParallelWritesKeepEveryKeyAndALinkedTree(BTreeLockMode lockMode, int size)
  {
    const int writerCount = 8;
    const int keysPerWriter = 25_000;
    var tree = CreateTree(lockMode, size);
    var seenBefore = new ConcurrentBag<int>();

    Parallel.For(0, writerCount, new ParallelOptions { MaxDegreeOfParallelism = writerCount }, writer =>
    {
      for (var i = 0; i < keysPerWriter; ++i)
      {
        // Every writer has its own keys, spread over the whole key range.
        var key = i * writerCount + writer;
        key = (int)((uint)key * 2654435761u >> 1);
        switch (i % 3)
        {
          case 0:
            tree.Upsert(key, key, out _);
            break;
          case 1:
            tree.TryInsert(key, key, out _);
            break;
          default:
            var value = key;
            tree.AddOrUpdate(key, (ref int x) => x = value, (ref int x) => x = -1, out _);
            break;
        }
        // A write is visible once it completes.
        Assert.That(tree.TryGetValue(in key, out var read), Is.True, $"key {key}");
        if (read != key)
          seenBefore.Add(key);
      }
    });

    Assert.That(seenBefore, Is.Empty);
    Assert.That(tree.Length, Is.EqualTo(writerCount * keysPerWriter));
    tree.Validate();
    tree.ValidateLeafs();
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public void WritesAtTheEndOfTheLastLeafDoNotWaitForALockedRoot(BTreeLockMode lockMode)
  {
    var tree = CreateTree(lockMode);
    // An ascending split of an 8 key leaf leaves 5 keys in the last leaf,
    // which is full again after 3 more. So 1001 keys leave it with room.
    for (var key = 0; key < 1001; ++key)
      tree.Upsert(key, key + 1, out _);
    var root = GetRoot(tree);
    Assert.That(root, Is.Not.InstanceOf<BTree<int, int>.LeafNode>());

    root.WriteLock();
    try
    {
      var writes = Task.Factory.StartNew(() =>
      {
        // The last key itself, and a new key after it.
        Assert.That(tree.Upsert(1000, 5, out _), Is.False);
        Assert.That(tree.Upsert(1001, 6, out _), Is.True);
      }, TaskCreationOptions.LongRunning);
      Assert.That(writes.Wait(TimeSpan.FromSeconds(10)), Is.True, "A write waits for the root.");
    }
    finally
    {
      root.WriteUnlock();
    }
    var updated = 1000;
    Assert.That(tree.TryGetValue(in updated, out var updatedValue), Is.True);
    Assert.That(updatedValue, Is.EqualTo(5));
    var added = 1001;
    Assert.That(tree.TryGetValue(in added, out var addedValue), Is.True);
    Assert.That(addedValue, Is.EqualTo(6));
    tree.Validate();
    tree.ValidateLeafs();
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public void WriteBeforeTheLastKeyDoesNotGoToTheLastLeafByMistake(BTreeLockMode lockMode)
  {
    var tree = CreateTree(lockMode);
    for (var key = 0; key < 1000; ++key)
      tree.Upsert(key * 10, key, out _);

    // Between keys of the last leaf, and before the last leaf.
    tree.Upsert(9985, 1, out _);
    tree.Upsert(5, 2, out _);

    var near = 9985;
    Assert.That(tree.TryGetValue(in near, out var nearValue), Is.True);
    Assert.That(nearValue, Is.EqualTo(1));
    var far = 5;
    Assert.That(tree.TryGetValue(in far, out var farValue), Is.True);
    Assert.That(farValue, Is.EqualTo(2));
    tree.Validate();
    tree.ValidateLeafs();
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public void AscendingAndRandomWritersKeepEveryKeyInOrder(BTreeLockMode lockMode)
  {
    const int writerCount = 8;
    const int keysPerWriter = 50_000;
    const int ascendingBase = 1 << 30;
    var tree = CreateTree(lockMode);
    var next = 0;

    Parallel.For(0, writerCount, new ParallelOptions { MaxDegreeOfParallelism = writerCount }, writer =>
    {
      for (var i = 0; i < keysPerWriter; ++i)
      {
        // Half of the writers append, and the others write below the appended keys.
        var key = writer % 2 == 0
            ? ascendingBase + Interlocked.Increment(ref next)
            : (int)((uint)(i * writerCount + writer) * 2654435761u >> 2);
        tree.Upsert(key, key, out _);
        Assert.That(tree.TryGetValue(in key, out var value), Is.True, $"key {key}");
        Assert.That(value, Is.EqualTo(key));
      }
    });

    Assert.That(tree.Length, Is.EqualTo(writerCount * keysPerWriter));
    tree.Validate();
    tree.ValidateLeafs();
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor, 8)]
  [TestCase(BTreeLockMode.NodeLevelMonitor, 128)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter, 8)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter, 128)]
  public void ParallelAscendingWritesCanFillANewLeafBeforeTheSplitterLocksIt(BTreeLockMode lockMode, int size)
  {
    const int writerCount = 8;
    const int count = 800_000;
    var tree = CreateTree(lockMode, size);
    var next = 0;

    // Every writer targets the newest leaf, so a leaf fills
    // up right after the split that created it.
    Parallel.For(0, writerCount, new ParallelOptions { MaxDegreeOfParallelism = writerCount }, writer =>
    {
      while (true)
      {
        var key = Interlocked.Increment(ref next);
        if (key > count)
          return;
        switch (key % 3)
        {
          case 0:
            tree.Upsert(key, key, out _);
            break;
          case 1:
            tree.TryInsert(key, key, out _);
            break;
          default:
            var value = key;
            tree.AddOrUpdate(key, (ref int x) => x = value, (ref int x) => x = -1, out _);
            break;
        }
        // A completed write is visible, even if a split of its leaf is still running.
        Assert.That(tree.TryGetValue(in key, out var read), Is.True, $"key {key}");
        Assert.That(read, Is.EqualTo(key));
      }
    });

    Assert.That(tree.Length, Is.EqualTo(count));
    tree.Validate();
    tree.ValidateLeafs();
  }

  struct FirstKeyVisitor : ISegmentRecordVisitor<int, int>
  {
    public int FirstKey;

    public SegmentVisitAction Visit(in int key, in int value)
    {
      FirstKey = key;
      return SegmentVisitAction.Stop;
    }
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor, 8)]
  [TestCase(BTreeLockMode.NodeLevelMonitor, 128)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter, 8)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter, 128)]
  public void CompletedAscendingWritesAreVisibleToEveryReadPath(BTreeLockMode lockMode, int size)
  {
    const int writerCount = 8;
    const int count = 200_000;
    var tree = CreateTree(lockMode, size);
    var next = 0;

    Parallel.For(0, writerCount, new ParallelOptions { MaxDegreeOfParallelism = writerCount }, writer =>
    {
      var iterator = new BTreeSeekableIterator<int, int>(tree);
      while (true)
      {
        var key = Interlocked.Increment(ref next);
        if (key > count)
          return;
        tree.Upsert(key, key + 1, out _);

        Assert.That(tree.ContainsKey(in key), Is.True, $"ContainsKey {key}");
        var visitor = new FirstKeyVisitor();
        tree.Visit(in key, ref visitor);
        Assert.That(visitor.FirstKey, Is.EqualTo(key), "Visit");
        Assert.That(iterator.SeekToFirstGreaterOrEqualElement(in key), Is.True, $"seek {key}");
        Assert.That(iterator.CurrentKey, Is.EqualTo(key), "seek");
        Assert.That(iterator.SeekToLastSmallerOrEqualElement(in key), Is.True, $"reverse seek {key}");
        Assert.That(iterator.CurrentKey, Is.EqualTo(key), "reverse seek");
        // The end of the tree is at or after every completed write.
        Assert.That(iterator.SeekEnd(), Is.True, $"end {key}");
        Assert.That(iterator.CurrentKey, Is.GreaterThanOrEqualTo(key), "end");
      }
    });

    tree.Validate();
    tree.ValidateLeafs();
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public void UpdatesOfOneKeyAreNotLostDuringSplits(BTreeLockMode lockMode)
  {
    const int counterKey = 1_000_000;
    const int incrementsPerWriter = 50_000;
    const int writerCount = 4;
    var tree = CreateTree(lockMode, 4);
    tree.Upsert(counterKey, 0, out _);

    using var stop = new CancellationTokenSource();
    // Inserts around the counter keep splitting the leaf that holds it.
    var splitter = Task.Factory.StartNew(() =>
    {
      for (var i = 1; !stop.IsCancellationRequested; ++i)
      {
        tree.Upsert(counterKey - i, i, out _);
        tree.Upsert(counterKey + i, i, out _);
      }
    }, TaskCreationOptions.LongRunning);
    Parallel.For(0, writerCount, new ParallelOptions { MaxDegreeOfParallelism = writerCount }, writer =>
    {
      for (var i = 0; i < incrementsPerWriter; ++i)
        tree.AddOrUpdate(counterKey, (ref int x) => x = 1, (ref int x) => ++x, out _);
    });
    stop.Cancel();
    splitter.Wait();

    var key = counterKey;
    Assert.That(tree.TryGetValue(in key, out var value), Is.True);
    Assert.That(value, Is.EqualTo(writerCount * incrementsPerWriter));
    tree.Validate();
    tree.ValidateLeafs();
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public void SplitsFinishWhileWritersKeepTheRightNeighbourBusy(BTreeLockMode lockMode)
  {
    const int splittingInserts = 20_000;
    const int hotKey = 1_000_000;
    var tree = CreateTree(lockMode, 4);
    // The hot keys share the leaf to the right of the leaf that keeps splitting.
    for (var key = hotKey; key < hotKey + 3; ++key)
      tree.Upsert(key, key, out _);

    using var stop = new CancellationTokenSource();
    var hotWriters = Enumerable.Range(0, 4).Select(writer => Task.Factory.StartNew(() =>
    {
      while (!stop.IsCancellationRequested)
        tree.Upsert(hotKey, 1, out _);
    }, TaskCreationOptions.LongRunning)).ToArray();

    var inserter = Task.Factory.StartNew(() =>
    {
      for (var key = 0; key < splittingInserts; ++key)
        tree.Upsert(key, key, out _);
    }, TaskCreationOptions.LongRunning);

    var finished = inserter.Wait(TimeSpan.FromSeconds(60));
    stop.Cancel();
    Task.WaitAll(hotWriters, TimeSpan.FromSeconds(60));

    Assert.That(finished, Is.True, "Splits starve while the right neighbour is busy.");
    Assert.That(tree.Length, Is.EqualTo(splittingInserts + 3));
    tree.Validate();
    tree.ValidateLeafs();
  }
}
