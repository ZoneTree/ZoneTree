using ZoneTree.Collections;
using ZoneTree.Comparers;

namespace ZoneTree.UnitTests;

public sealed class BPlusTreeThreadSafetyTests
{
  [TestCase(3_000_000, BPlusTreeLockMode.TopLevelReaderWriter)]
  [TestCase(3_000_000, BPlusTreeLockMode.TopLevelMonitor)]
  [TestCase(3_000_000, BPlusTreeLockMode.NodeLevelMonitor)]
  [TestCase(3_000_000, BPlusTreeLockMode.NodeLevelReaderWriter)]
  public void MassiveInsertsAndReads(int count, BPlusTreeLockMode lockMode)
  {
    var readCount = 0;
    var tree = new BPlusTree<long, long>(new Int64ComparerAscending(), lockMode);
    var task1 = Parallel.ForEachAsync(Enumerable.Range(0, count), (i, t) =>
    {
      tree.TryInsert(i, i, out _);
      return ValueTask.CompletedTask;
    });
    Thread.Sleep(1);
    var task2 = Parallel.ForEachAsync(Enumerable.Range(0, count), (i, t) =>
    {
      if (tree.TryGetValue(i, out var j))
      {
        if (i != j)
          throw new Exception($"{i} != {j}");
        Interlocked.Increment(ref readCount);
      }
      return ValueTask.CompletedTask;
    });
    task1.Wait();
    task2.Wait();
    Console.WriteLine("Read Count: " + readCount);
  }
}
