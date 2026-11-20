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
public class LongTreeBenchmark
{
  readonly int Count = 1_000_000;
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
  public void InsertBPlusTree() => RandomLongInserts.InsertBPlusTree(Data);

  [Benchmark]
  public void InsertSortedDictionary() => RandomLongInserts.InsertSortedDictionary(Data);

}
