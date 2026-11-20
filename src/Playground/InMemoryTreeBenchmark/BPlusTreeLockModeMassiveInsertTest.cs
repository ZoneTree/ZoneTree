using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using ZoneTree.Collections;
using ZoneTree.Comparers;

namespace Playground.InMemoryTreeBenchmark;

[HtmlExporter]
[SimpleJob(RunStrategy.ColdStart, iterationCount: 1)]
[MinColumn, MaxColumn, MeanColumn, MedianColumn, /*AllStatisticsColumn*/]
[MemoryDiagnoser]
/*[HardwareCounters(
    HardwareCounter.TotalCycles,
    HardwareCounter.TotalIssues,
    HardwareCounter.CacheMisses,
    HardwareCounter.Timer)]*/
public class BPlusTreeLockModeMassiveInsertTest
{
  readonly int Count = 3_000_000;
  readonly bool Shuffled = true;

  [GlobalSetup]
  public void Setup()
  {
    Data = Shuffled ?
        RandomLongInserts.GetRandomArray(Count) :
        RandomLongInserts.GetSortedArray(Count);
  }

  long[] Data = Array.Empty<long>();

  [Benchmark]
  public void TopLevelMonitor() =>
      MassiveInsertsAndReads(Data, BPlusTreeLockMode.TopLevelMonitor);

  [Benchmark]
  public void TopLevelReaderWriter() =>
      MassiveInsertsAndReads(Data, BPlusTreeLockMode.TopLevelReaderWriter);

  [Benchmark]
  public void NodeLevelMonitor() =>
      MassiveInsertsAndReads(Data, BPlusTreeLockMode.NodeLevelMonitor);

  [Benchmark]
  public void NodeLevelReaderWriter() =>
      MassiveInsertsAndReads(Data, BPlusTreeLockMode.NodeLevelReaderWriter);

  public static void MassiveInsertsAndReads(long[] arr, BPlusTreeLockMode lockMode = BPlusTreeLockMode.NodeLevelMonitor)
  {
    var readCount = 0;
    var tree = new BPlusTree<long, long>(new Int64ComparerAscending(),
        lockMode);
    var task1 = Parallel.ForEachAsync(arr, (i, t) =>
    {
      tree.TryInsert(i, i, out _);
      return ValueTask.CompletedTask;
    });
    Thread.Sleep(1);
    var task2 = Parallel.ForEachAsync(arr, (i, t) =>
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
  }
}
