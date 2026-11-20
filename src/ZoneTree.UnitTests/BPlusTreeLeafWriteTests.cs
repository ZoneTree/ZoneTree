using System.Collections.Concurrent;
using System.Reflection;
using ZoneTree.Collections;
using ZoneTree.Comparers;

namespace ZoneTree.UnitTests;

/// <summary>
/// Writes find their leaf without locking the inner nodes and lock only the
/// leaf, unless the leaf is full and has to split.
/// </summary>
public sealed class BPlusTreeLeafWriteTests
{
  static BPlusTree<int, int> CreateTree(BPlusTreeLockMode lockMode, int size = 8)
  {
    return new BPlusTree<int, int>(new Int32ComparerAscending(), lockMode, nodeSize: size, leafSize: size);
  }

  static BPlusTree<int, int>.Node GetRoot(BPlusTree<int, int> tree)
  {
    return (BPlusTree<int, int>.Node)typeof(BPlusTree<int, int>)
        .GetField("Root", BindingFlags.Instance | BindingFlags.NonPublic)
        .GetValue(tree);
  }

  static BPlusTree<int, int>.LeafNode GetFirstLeaf(BPlusTree<int, int> tree)
  {
    return (BPlusTree<int, int>.LeafNode)typeof(BPlusTree<int, int>)
        .GetField("FirstLeafNode", BindingFlags.Instance | BindingFlags.NonPublic)
        .GetValue(tree);
  }

  static BPlusTree<int, int>.LeafNode GetLastLeaf(BPlusTree<int, int> tree)
  {
    return (BPlusTree<int, int>.LeafNode)typeof(BPlusTree<int, int>)
        .GetField("LastLeafNode", BindingFlags.Instance | BindingFlags.NonPublic)
        .GetValue(tree);
  }

  static List<int> GetLeafLengths(BPlusTree<int, int> tree)
  {
    var lengths = new List<int>();
    for (var leaf = GetFirstLeaf(tree); leaf != null; leaf = leaf.Next)
      lengths.Add(leaf.Length);
    return lengths;
  }

  public enum WriteMethod
  {
    Upsert,
    UpsertWithValueGetter,
    TryInsert,
    AddOrUpdate,
  }

  static void Write(BPlusTree<int, int> tree, WriteMethod method, int key)
  {
    switch (method)
    {
      case WriteMethod.Upsert:
        tree.Upsert(key, key, out _);
        break;
      case WriteMethod.UpsertWithValueGetter:
        tree.Upsert(key, _ => key, out _, out _);
        break;
      case WriteMethod.TryInsert:
        tree.TryInsert(key, key, out _);
        break;
      case WriteMethod.AddOrUpdate:
        tree.AddOrUpdate(key, (ref int value) => value = key, (ref int value) => value = -1, out _);
        break;
    }
  }

  [Test]
  public void AscendingWritesFillTheLeaves(
      [Values] BPlusTreeLockMode lockMode, [Values] WriteMethod method)
  {
    var tree = CreateTree(lockMode);
    for (var key = 0; key < 1000; ++key)
      Write(tree, method, key);
    // A split of the last leaf for a key after its last key moves only its last key.
    var lengths = GetLeafLengths(tree);
    Assert.That(lengths.Take(lengths.Count - 1), Is.All.EqualTo(7));
    Assert.That(lengths.Sum(), Is.EqualTo(1000));
    tree.Validate();
    tree.ValidateLeaves();
  }

  [Test]
  public void DescendingWritesFillTheLeaves(
      [Values] BPlusTreeLockMode lockMode, [Values] WriteMethod method)
  {
    var tree = CreateTree(lockMode);
    for (var key = 999; key >= 0; --key)
      Write(tree, method, key);
    // A split of the first leaf for a key before its first key moves only its first key.
    var lengths = GetLeafLengths(tree);
    Assert.That(lengths.Skip(1), Is.All.EqualTo(7));
    Assert.That(lengths.Sum(), Is.EqualTo(1000));
    tree.Validate();
    tree.ValidateLeaves();
  }

  [Test]
  public void WritesBetweenTheKeysOfTheFirstAndLastLeafSplitItInTheMiddle([Values] WriteMethod method)
  {
    var tree = CreateTree(BPlusTreeLockMode.NodeLevelMonitor);
    for (var key = 0; key < 8; ++key)
      Write(tree, method, key * 10);
    Write(tree, method, 65);
    Assert.That(GetLeafLengths(tree), Is.EqualTo(new[] { 4, 5 }));
    tree.Validate();
    tree.ValidateLeaves();
  }

  /// <summary>
  /// Throws when it compares the two keys, in either order.
  /// </summary>
  sealed class FailingPairComparer : IRefComparer<int>
  {
    public int First;

    public int Second;

    public volatile bool IsFailing;

    public int Compare(in int x, in int y)
    {
      if (IsFailing && (x == First && y == Second || x == Second && y == First))
        throw new InvalidOperationException("The comparer failed.");
      return x.CompareTo(y);
    }
  }

  [TestCase(BPlusTreeLockMode.NodeLevelMonitor, false)]
  [TestCase(BPlusTreeLockMode.NodeLevelMonitor, true)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter, false)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter, true)]
  public void ComparerFailureWhileSplittingAnEndLeafReleasesIt(BPlusTreeLockMode lockMode, bool descending)
  {
    var comparer = new FailingPairComparer();
    var tree = new BPlusTree<int, int>(comparer, lockMode, nodeSize: 8, leafSize: 8);
    BPlusTree<int, int>.LeafNode GetEndLeaf() => descending ? GetFirstLeaf(tree) : GetLastLeaf(tree);
    var step = descending ? -1 : 1;
    var key = 0;
    var count = 0;
    while (count < 100 || !GetEndLeaf().IsFull)
    {
      tree.Upsert(key, key, out _);
      key += step;
      ++count;
    }
    var endLeaf = GetEndLeaf();
    comparer.First = key;
    comparer.Second = descending ? endLeaf.Keys[0] : endLeaf.Keys[endLeaf.Length - 1];
    comparer.IsFailing = true;

    // The split compares the key with the end key of the leaf it holds,
    // and the write in the new leaf compares them again.
    Assert.Throws<InvalidOperationException>(() => tree.Upsert(key, key, out _));
    comparer.IsFailing = false;

    // A monitor is reentrant, so another thread checks that the leaf is free.
    var write = Task.Factory.StartNew(
        () => tree.Upsert(key, key, out _), TaskCreationOptions.LongRunning);
    Assert.That(write.Wait(TimeSpan.FromSeconds(30)), Is.True, "The failed write keeps a leaf locked.");
    Assert.That(tree.Length, Is.EqualTo(count + 1));
    tree.Validate();
    tree.ValidateLeaves();
  }

  [TestCase(BPlusTreeLockMode.NodeLevelMonitor)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter)]
  public void WritesToALeafWithRoomDoNotWaitForALockedRoot(BPlusTreeLockMode lockMode)
  {
    var tree = CreateTree(lockMode);
    for (var key = 0; key < 1000; ++key)
      tree.Upsert(key * 10, key, out _);
    // Ascending inserts leave 7 keys in the first leaf. The first write fills it,
    // and the second splits it in the middle, because it goes between its keys.
    tree.Upsert(5, 5, out _);
    tree.Upsert(15, 15, out _);
    Assert.That(GetFirstLeaf(tree).Length, Is.EqualTo(5));
    var root = GetRoot(tree);
    Assert.That(root, Is.Not.InstanceOf<BPlusTree<int, int>.LeafNode>());

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

  [TestCase(BPlusTreeLockMode.NodeLevelMonitor)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter)]
  public void WriteToAFullLeafGoesThroughTheRoot(BPlusTreeLockMode lockMode)
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
    tree.ValidateLeaves();
  }

  [Test]
  public void WriterWaitingForALeafThatSplitsWritesToTheNewLeaf()
  {
    // A monitor is reentrant, so this thread can split the leaf it holds.
    var tree = CreateTree(BPlusTreeLockMode.NodeLevelMonitor);
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
    tree.ValidateLeaves();
  }

  [TestCase(BPlusTreeLockMode.NodeLevelMonitor, 4)]
  [TestCase(BPlusTreeLockMode.NodeLevelMonitor, 128)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter, 4)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter, 128)]
  [TestCase(BPlusTreeLockMode.TopLevelMonitor, 4)]
  [TestCase(BPlusTreeLockMode.TopLevelReaderWriter, 4)]
  public void ParallelWritesKeepEveryKeyAndALinkedTree(BPlusTreeLockMode lockMode, int size)
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
    tree.ValidateLeaves();
  }

  [TestCase(BPlusTreeLockMode.NodeLevelMonitor)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter)]
  public void WritesAtTheEndOfTheLastLeafDoNotWaitForALockedRoot(BPlusTreeLockMode lockMode)
  {
    var tree = CreateTree(lockMode);
    // An ascending split of the full 8 key last leaf moves only its last key to
    // the new last leaf, so the leaves after the first one start at multiples of 7,
    // and 1001 keys leave 7 keys in the last one.
    for (var key = 0; key < 1001; ++key)
      tree.Upsert(key, key + 1, out _);
    Assert.That(GetLastLeaf(tree).Length, Is.EqualTo(7));
    var root = GetRoot(tree);
    Assert.That(root, Is.Not.InstanceOf<BPlusTree<int, int>.LeafNode>());

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
    tree.ValidateLeaves();
  }

  [TestCase(BPlusTreeLockMode.NodeLevelMonitor)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter)]
  public void WriteBeforeTheLastKeyDoesNotGoToTheLastLeafByMistake(BPlusTreeLockMode lockMode)
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
    tree.ValidateLeaves();
  }

  [TestCase(BPlusTreeLockMode.NodeLevelMonitor)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter)]
  public void AscendingAndRandomWritersKeepEveryKeyInOrder(BPlusTreeLockMode lockMode)
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
    tree.ValidateLeaves();
  }

  [TestCase(BPlusTreeLockMode.NodeLevelMonitor, 8)]
  [TestCase(BPlusTreeLockMode.NodeLevelMonitor, 128)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter, 8)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter, 128)]
  public void ParallelAscendingWritesCanFillANewLeafBeforeTheSplitterLocksIt(BPlusTreeLockMode lockMode, int size)
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
    tree.ValidateLeaves();
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

  [TestCase(BPlusTreeLockMode.NodeLevelMonitor, 8)]
  [TestCase(BPlusTreeLockMode.NodeLevelMonitor, 128)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter, 8)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter, 128)]
  public void CompletedAscendingWritesAreVisibleToEveryReadPath(BPlusTreeLockMode lockMode, int size)
  {
    const int writerCount = 8;
    const int count = 200_000;
    var tree = CreateTree(lockMode, size);
    var next = 0;

    Parallel.For(0, writerCount, new ParallelOptions { MaxDegreeOfParallelism = writerCount }, writer =>
    {
      var iterator = new BPlusTreeSeekableIterator<int, int>(tree);
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
    tree.ValidateLeaves();
  }

  [TestCase(BPlusTreeLockMode.NodeLevelMonitor)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter)]
  public void UpdatesOfOneKeyAreNotLostDuringSplits(BPlusTreeLockMode lockMode)
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
    tree.ValidateLeaves();
  }

  [TestCase(BPlusTreeLockMode.NodeLevelMonitor)]
  [TestCase(BPlusTreeLockMode.NodeLevelReaderWriter)]
  public void SplitsFinishWhileWritersKeepTheRightNeighbourBusy(BPlusTreeLockMode lockMode)
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
    tree.ValidateLeaves();
  }
}
