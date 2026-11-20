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
public class ParallelMassiveInsertTests
{
  readonly int Count = 3_000_000;
  readonly bool Shuffled = true;

  static readonly BPlusTreeLockMode BPlusTreeLockMode = BPlusTreeLockMode.NodeLevelMonitor;

  [GlobalSetup]
  public void Setup()
  {
    Data = Shuffled ?
        RandomLongInserts.GetRandomArray(Count) :
        RandomLongInserts.GetSortedArray(Count);
  }

  public long[] Data = Array.Empty<long>();

  [Benchmark]
  public void Parallel_BPlusTree() => MassiveInsertsAndReadsBPlusTree();

  public void MassiveInsertsAndReadsBPlusTree()
  {
    var tree = new BPlusTree<long, long>(new Int64ComparerAscending(), BPlusTreeLockMode);
    var task1 = Parallel.ForEachAsync(Enumerable.Range(0, Count), (i, t) =>
    {
      tree.TryInsert(i, i, out _);
      return ValueTask.CompletedTask;
    });
    var task2 = Parallel.ForEachAsync(Enumerable.Range(0, Count), (i, t) =>
    {
      tree.TryGetValue(i, out var j);
      return ValueTask.CompletedTask;
    });
    task1.Wait();
    task2.Wait();
  }
}
