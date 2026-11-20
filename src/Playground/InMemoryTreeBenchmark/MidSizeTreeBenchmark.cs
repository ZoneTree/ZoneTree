using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;

namespace Playground.InMemoryTreeBenchmark;

[HtmlExporter]
[SimpleJob(RunStrategy.ColdStart, iterationCount: 1)]
[MinColumn, MaxColumn, MeanColumn, MedianColumn, /*AllStatisticsColumn*/]
[MemoryDiagnoser]
[HardwareCounters(
    HardwareCounter.TotalCycles,
    HardwareCounter.TotalIssues,
    HardwareCounter.CacheMisses,
    HardwareCounter.Timer)]
public class MidSizeTreeBenchmark
{
  readonly int Count = 1_000_000;
  readonly bool Shuffled = true;

  [GlobalSetup]
  public void Setup()
  {
    Data = Shuffled ?
        RandomMidSizeInserts.GetRandomArray(Count) :
        RandomMidSizeInserts.GetSortedArray(Count);
  }

  MidSizeKey[] Data = [];

  [Benchmark]
  public void InsertBPlusTree() => RandomMidSizeInserts.InsertBPlusTree(Data);

  [Benchmark]
  public void InsertSortedDictionary() => RandomMidSizeInserts.InsertSortedDictionary(Data);

}
