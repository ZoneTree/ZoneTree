using Humanizer;
using System.Diagnostics;
using ZoneTree.Options;
using ZoneTree.Transactional;

namespace Playground.Benchmark;

public sealed class ZoneTreeTest3 : ZoneTreeTestBase<int, int>
{
  const string FolderName = "transactional-int-int";

  public override string DataPath =>
      RootPath + WALMode + "-" + Count.ToHuman()
          + "_" + CompressionMethod + "_"
          + FolderName;

  public void Insert(IStatsCollector stats)
  {
    stats.Name = "Insert";
    AddOptions(stats);
    var count = Count;
    stats.LogWithColor(GetLabel("Insert Transactional <int, int>"), ConsoleColor.Cyan);

    if (TestConfig.RecreateDatabases && Directory.Exists(DataPath))
      Directory.Delete(DataPath, true);

    stats.RestartStopwatch();

    using var zoneTree = OpenOrCreateTransactionalZoneTree();
    using var maintainer = zoneTree.CreateMaintainer();
    maintainer.ThresholdForMergeOperationStart = TestConfig.ThresholdForMergeOperationStart;
    stats.AddStage("Loaded In");

    void Insert(int x)
    {
      using var tx = zoneTree.BeginTransaction(TransactionIsolationLevel.Snapshot);
      tx.Upsert(x, x + x);
      tx.Commit();
    }

    if (TestConfig.EnableParalelInserts)
    {
      Parallel.For(0, count, Insert);
    }
    else
    {
      for (var x = 0; x < count; ++x)
      {
        Insert(x);
      }
    }
    stats.AddStage("Inserted In", ConsoleColor.Green);

    if (WALMode == WriteAheadLogMode.None)
    {
      zoneTree.Maintenance.ZoneTree.Maintenance.MoveMutableSegmentForward();
      zoneTree.Maintenance.ZoneTree.Maintenance.StartMergeOperation()?.Join();
    }
    maintainer.WaitForBackgroundThreads();

    stats.AddStage("Merged In", ConsoleColor.DarkCyan);
  }

  public void Iterate(IStatsCollector stats)
  {
    stats.Name = "Iterate";
    var count = Count;
    stats.LogWithColor(GetLabel("Iterate Transactional <int, int>"), ConsoleColor.Cyan);
    stats.RestartStopwatch();

    using var zoneTree = OpenOrCreateTransactionalZoneTree();
    using var maintainer = zoneTree.CreateMaintainer();
    maintainer.ThresholdForMergeOperationStart = TestConfig.ThresholdForMergeOperationStart;

    stats.AddStage("Loaded in", ConsoleColor.DarkYellow);

    var off = 0;
    using var tx = zoneTree.BeginReadOnlyTransaction();
    using var iterator = tx.CreateIterator();
    while (iterator.Next())
    {
      if (iterator.CurrentKey * 2 != iterator.CurrentValue)
        throw new Exception("invalid key or value");
      ++off;
    }
    if (off != count)
      throw new Exception($"missing records. {off} != {count}");

    stats.AddStage(
        "Iterated in",
        ConsoleColor.Green);
    maintainer.WaitForBackgroundThreads();
  }
}
