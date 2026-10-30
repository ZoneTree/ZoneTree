using ZoneTree.Collections.BTree;
using ZoneTree.Collections.BTree.Lock;
using ZoneTree.Comparers;

namespace ZoneTree.UnitTests;

public sealed class BTreeConcurrentLeafWriteTests
{
  [TestCase(BTreeLockMode.NodeLevelMonitor, 4)]
  [TestCase(BTreeLockMode.NodeLevelMonitor, 32)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter, 4)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter, 32)]
  public async Task ParallelLeafWritesPreserveBothIteratorDirections(
      BTreeLockMode lockMode, int nodeSize)
  {
    const int count = 20_000;
    var tree = new BTree<int, long>(new Int32ComparerAscending(), lockMode,
        nodeSize: nodeSize, leafSize: nodeSize);
    for (var key = 0; key < count; key += 4)
      tree.Upsert(key, Value(key), out _);

    using var start = new Barrier(3);
    var finished = 0;
    var writer = Task.Run(() =>
    {
      start.SignalAndWait();
      try
      {
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
          // 7919 and count are coprime, so every key is visited exactly once.
          var key = (i * 7919) % count;
          switch (i % 4)
          {
            case 0:
              tree.Upsert(key, Value(key), out _);
              break;
            case 1:
              tree.TryInsert(key, Value(key), out _);
              break;
            case 2:
              tree.AddOrUpdate(key,
                  (ref long value) => value = Value(key),
                  (ref long value) => value = Value(key), out _);
              break;
            default:
              tree.Upsert(key, _ => Value(key), out _, out _);
              break;
          }
        });
      }
      finally { Volatile.Write(ref finished, 1); }
    });

    Task Read(bool reverse) => Task.Run(() =>
    {
      start.SignalAndWait();
      do
      {
        var minimumCount = tree.Length;
        var iterator = new BTreeSeekableIterator<int, long>(tree);
        var found = reverse ? iterator.SeekEnd() : iterator.SeekBegin();
        var previous = reverse ? int.MaxValue : int.MinValue;
        var seen = 0;
        while (found)
        {
          var key = iterator.CurrentKey;
          Assert.That(reverse ? key < previous : key > previous, Is.True,
              "Concurrent splits must not repeat or reorder records.");
          Assert.That(iterator.CurrentValue, Is.EqualTo(Value(key)),
              "The copied key and value must belong to the same record.");
          previous = key;
          ++seen;
          found = reverse ? iterator.Prev() : iterator.Next();
        }
        Assert.That(seen, Is.GreaterThanOrEqualTo(minimumCount),
            "Every key present before the scan must remain reachable.");
      } while (Volatile.Read(ref finished) == 0);
    });

    await Task.WhenAll(writer, Read(false), Read(true)).WaitAsync(TimeSpan.FromSeconds(30));
    tree.Validate();
    tree.ValidateLeafs();
    Assert.That(tree.Length, Is.EqualTo(count));
    for (var key = 0; key < count; ++key)
    {
      Assert.That(tree.TryGetValue(key, out var value), Is.True);
      Assert.That(value, Is.EqualTo(Value(key)));
    }
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public void ConcurrentAtomicUpdatesSurviveRootAndLeafReplacement(BTreeLockMode lockMode)
  {
    const int count = 20_000;
    var tree = new BTree<int, int>(new Int32ComparerAscending(), lockMode,
        nodeSize: 4, leafSize: 4);
    tree.Upsert(-1, 0, out _);
    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, key =>
    {
      tree.Upsert(key, key, out _);
      tree.AddOrUpdate(-1, (ref int value) => value = 1,
          (ref int value) => ++value, out _);
    });
    Assert.That(tree.TryGetValue(-1, out var actual), Is.True);
    Assert.That(actual, Is.EqualTo(count));
    Assert.That(tree.LastOpIndex, Is.EqualTo(2L * count + 1));
    tree.Validate();
    tree.ValidateLeafs();
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  public async Task ThrowingCallbacksReleaseAFullLeafForOtherWriters(BTreeLockMode lockMode)
  {
    var tree = new BTree<int, int>(new Int32ComparerAscending(), lockMode,
        nodeSize: 4, leafSize: 4);
    for (var key = 0; key < 4; ++key)
      tree.Upsert(key, key, out _);

    Assert.Throws<InvalidOperationException>(() =>
        tree.Upsert(2, _ => throw new InvalidOperationException(), out _, out _));
    Assert.Throws<InvalidOperationException>(() =>
        tree.AddOrUpdate(2,
            (ref int _) => throw new InvalidOperationException(),
            (ref int _) => throw new InvalidOperationException(), out _));

    await Task.Run(() => tree.Upsert(2, 42, out _)).WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(tree.TryGetValue(2, out var actual), Is.True);
    Assert.That(actual, Is.EqualTo(42));
  }

  static long Value(int key) => 7L * key + 1;

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  [TestCase(BTreeLockMode.TopLevelMonitor)]
  [TestCase(BTreeLockMode.TopLevelReaderWriter)]
  public async Task ThrowingInsertionCallbacksReleaseSplitLocks(BTreeLockMode lockMode)
  {
    foreach (var useAdder in new[] { false, true })
    {
      var tree = new BTree<int, int>(new Int32ComparerAscending(), lockMode,
          nodeSize: 4, leafSize: 4);
      for (var key = 0; key < 4; ++key)
        tree.Upsert(key, key, out _);

      if (useAdder)
        Assert.Throws<InvalidOperationException>(() =>
            tree.AddOrUpdate(5,
                (ref int _) => throw new InvalidOperationException(),
                (ref int _) => throw new InvalidOperationException(), out _));
      else
        Assert.Throws<InvalidOperationException>(() =>
            tree.Upsert(5, _ => throw new InvalidOperationException(), out _, out _));

      await Task.Run(() => tree.Upsert(5, 42, out _)).WaitAsync(TimeSpan.FromSeconds(10));
      Assert.That(tree.TryGetValue(5, out var actual), Is.True);
      Assert.That(actual, Is.EqualTo(42));
      Assert.That(tree.Length, Is.EqualTo(5));
      tree.Validate();
    }
  }

  [TestCase(BTreeLockMode.NodeLevelMonitor)]
  [TestCase(BTreeLockMode.NodeLevelReaderWriter)]
  [TestCase(BTreeLockMode.TopLevelMonitor)]
  [TestCase(BTreeLockMode.TopLevelReaderWriter)]
  public async Task ThrowingCallbacksBelowTheRootReleaseTheLeaf(BTreeLockMode lockMode)
  {
    var tree = new BTree<int, int>(new Int32ComparerAscending(), lockMode,
        nodeSize: 4, leafSize: 4);
    for (var key = 0; key < 64; key += 2)
      tree.Upsert(key, key, out _);

    // An existing key reaches the updater, a new key the adder.
    // Both reach the value getter.
    foreach (var key in new[] { 20, 21 })
    {
      Assert.Throws<InvalidOperationException>(() =>
          tree.AddOrUpdate(key,
              (ref int _) => throw new InvalidOperationException(),
              (ref int _) => throw new InvalidOperationException(), out _));
      Assert.Throws<InvalidOperationException>(() =>
          tree.Upsert(key, _ => throw new InvalidOperationException(), out _, out _));
    }

    await Task.Run(() =>
    {
      tree.Upsert(20, 42, out _);
      tree.Upsert(21, 43, out _);
    }).WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(tree.TryGetValue(20, out var updated), Is.True);
    Assert.That(updated, Is.EqualTo(42));
    Assert.That(tree.TryGetValue(21, out var added), Is.True);
    Assert.That(added, Is.EqualTo(43));
    Assert.That(tree.Length, Is.EqualTo(33));
    tree.Validate();
  }
}
