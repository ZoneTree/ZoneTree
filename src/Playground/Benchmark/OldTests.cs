using System.Diagnostics;
using ZoneTree;
using ZoneTree.Options;

namespace Playground.Benchmark;

public sealed class OldTests
{
  const string DataPath = "../../data/";
  const string FolderName = "-int-int";

  private static string GetDataPath(WriteAheadLogMode mode, int count)
  {
    return DataPath + mode + "-" + count / 1_000_000.0 + "M" + FolderName;
  }

  public static void Insert(WriteAheadLogMode mode, int count)
  {
    var recCount = count / 1000000.0 + "M";
    Console.WriteLine("\r\n--------------------------");
    new StatsCollector().LogWithColor($"\r\n{mode} Insert <int,int> {recCount}\r\n", ConsoleColor.Cyan);
    string dataPath = GetDataPath(mode, count);
    if (TestConfig.RecreateDatabases && Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    var stopWatch = new Stopwatch();
    stopWatch.Start();
    using var zoneTree = OpenOrCreateZoneTree(mode, dataPath);
    using var basicMaintainer = zoneTree.CreateMaintainer();
    basicMaintainer.ThresholdForMergeOperationStart = TestConfig.ThresholdForMergeOperationStart;
    new StatsCollector().LogWithColor(
        "Loaded in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.DarkYellow);

    if (TestConfig.EnableParalelInserts)
    {
      Parallel.For(0, count, (x) =>
      {
        zoneTree.Upsert(x, x + x);
      });
    }
    else
    {
      for (var x = 0; x < count; ++x)
      {
        zoneTree.Upsert(x, x + x);
      }
    }

    new StatsCollector().LogWithColor(
        "Completed in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.Green);
    stopWatch.Restart();

    if (mode == WriteAheadLogMode.None)
    {
      zoneTree.Maintenance.MoveMutableSegmentForward();
      zoneTree.Maintenance.StartMergeOperation()?.Join();
    }
    basicMaintainer.WaitForBackgroundThreads();
    new StatsCollector().LogWithColor(
        "Merged in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.DarkCyan);
  }

  public static void MergeBottomSegment(WriteAheadLogMode mode, int count, int from, int to)
  {
    var recCount = count / 1000000.0 + "M";
    new StatsCollector().LogWithColor($"\r\n{mode} MergeBottomSegments <int,int> {recCount}\r\n", ConsoleColor.Cyan);

    string dataPath = GetDataPath(mode, count);
    var stopWatch = new Stopwatch();
    stopWatch.Start();
    using var zoneTree = OpenOrCreateZoneTree(mode, dataPath);
    new StatsCollector().LogWithColor(
        "Loaded in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.DarkYellow);
    zoneTree.Maintenance.StartBottomSegmentsMergeOperation(from, to).Join();
    ShowBottomSegments(zoneTree);

    new StatsCollector().LogWithColor(
        "Completed in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.Green);
  }

  private static void ShowBottomSegments(IZoneTree<int, int> z)
  {
    var ds = z.Maintenance.DiskSegment;
    Console.WriteLine($"------------------------------");
    Console.WriteLine($"Length \t\t Segment Id");
    Console.WriteLine($"------------------------------");
    Console.WriteLine($"Disk segment:");
    Console.WriteLine($"------------------------------");
    Console.WriteLine($"{ds.Length} \t {ds.SegmentId}");
    Console.WriteLine($"------------------------------");
    Console.WriteLine("Bottom Segments:");
    Console.WriteLine($"------------------------------");
    var bos = z.Maintenance.BottomSegments;
    foreach (var bs in bos)
    {
      Console.WriteLine($"{bs.Length} \t {bs.SegmentId}");
    }
    Console.WriteLine($"------------------------------");
  }

  public static void ShowBottomSegments(WriteAheadLogMode mode, int count)
  {
    var recCount = count / 1000000.0 + "M";
    new StatsCollector().LogWithColor($"\r\n{mode} ShowBottomSegments <int,int> {recCount}\r\n", ConsoleColor.Cyan);

    string dataPath = GetDataPath(mode, count);
    var stopWatch = new Stopwatch();
    stopWatch.Start();
    using var zoneTree = OpenOrCreateZoneTree(mode, dataPath);

    new StatsCollector().LogWithColor(
        "Loaded in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.DarkYellow);
    var iterate = false;
    if (iterate)
    {
      var random = Random.Shared;
      Parallel.For(0, 750000, (i) =>
      {
        var key = random.Next(0, 999_999_999);
        using var it = zoneTree.CreateReverseIterator();
        it.Seek(key);
        it.Next();
        if (it.CurrentKey != key)
          throw new Exception(it.CurrentKey + " != " + key);

        using var it2 = zoneTree.CreateIterator();
        it2.Seek(key);
        it2.Next();
        if (it2.CurrentKey != key)
          throw new Exception(it2.CurrentKey + " != " + key);

        if (!zoneTree.ContainsKey(key))
          throw new Exception($"{key} not found.");
        if (!zoneTree.TryGet(key, out var value))
          throw new Exception($"{key} not found.");
        if (value != 2 * key)
          throw new Exception($"{key} != {value / 2}");
      }
      );
    }
    ShowBottomSegments(zoneTree);

    new StatsCollector().LogWithColor(
        "Completed in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.Green);

  }

  public static void InsertSingleAndMerge(WriteAheadLogMode mode, int count, int key, int amount)
  {
    var recCount = count / 1000000.0 + "M";
    Console.WriteLine("\r\n--------------------------");
    new StatsCollector().LogWithColor($"\r\n{mode} Single Insert and Merge <int,int> {recCount}\r\n", ConsoleColor.Cyan);
    string dataPath = GetDataPath(mode, count);
    var stopWatch = new Stopwatch();
    stopWatch.Start();
    using var zoneTree = OpenOrCreateZoneTree(mode, dataPath);
    using var basicMaintainer = zoneTree.CreateMaintainer();
    basicMaintainer.ThresholdForMergeOperationStart = TestConfig.ThresholdForMergeOperationStart;
    new StatsCollector().LogWithColor(
        "Loaded in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.DarkYellow);

    for (var i = 0; i < amount; ++i)
    {
      zoneTree.Upsert(key, key + key);
      ++key;
    }
    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation()?.Join();

    basicMaintainer.WaitForBackgroundThreads();
    new StatsCollector().LogWithColor(
        "Merged in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.Green);
  }

  public static void Iterate(WriteAheadLogMode mode, int count)
  {
    var recCount = count / 1000000.0 + "M";
    new StatsCollector().LogWithColor($"\r\n{mode} Iterate <int,int> {recCount}\r\n", ConsoleColor.Cyan);

    string dataPath = GetDataPath(mode, count);
    var stopWatch = new Stopwatch();
    stopWatch.Start();
    using var zoneTree = OpenOrCreateZoneTree(mode, dataPath);
    using var basicMaintainer = zoneTree.CreateMaintainer();
    basicMaintainer.ThresholdForMergeOperationStart = TestConfig.ThresholdForMergeOperationStart;

    new StatsCollector().LogWithColor(
        "Loaded in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.DarkYellow);

    var off = 0;
    using var iterator = zoneTree.CreateIterator();
    while (iterator.Next())
    {
      if (iterator.CurrentKey * 2 != iterator.CurrentValue)
        throw new Exception("invalid key or value");
      ++off;
    }
    if (off != count)
      throw new Exception($"missing records. {off} != {count}");

    new StatsCollector().LogWithColor(
        "Completed in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.Green);
    basicMaintainer.WaitForBackgroundThreads();
  }

  public static void MultipleIterate(WriteAheadLogMode mode, int count, int iteratorCount)
  {
    var recCount = count / 1000000.0 + "M";
    new StatsCollector().LogWithColor($"\r\n{mode} Iterate <int,int> {recCount}\r\n", ConsoleColor.Cyan);

    string dataPath = GetDataPath(mode, count);
    var stopWatch = new Stopwatch();
    stopWatch.Start();
    using var zoneTree = OpenOrCreateZoneTree(mode, dataPath);
    using var basicMaintainer = zoneTree.CreateMaintainer();
    basicMaintainer.ThresholdForMergeOperationStart = TestConfig.ThresholdForMergeOperationStart;

    new StatsCollector().LogWithColor(
        "Loaded in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.DarkYellow);

    Parallel.For(0, iteratorCount, (x) =>
    {
      var off = 0;
      using var iterator = zoneTree.CreateIterator();
      while (iterator.Next())
      {
        if (iterator.CurrentKey * 2 != iterator.CurrentValue)
          throw new Exception("invalid key or value");
        ++off;
      }
      if (off != count)
        throw new Exception($"missing records. {off} != {count} TID:"
                + Environment.CurrentManagedThreadId);
    });


    new StatsCollector().LogWithColor(
        "Completed in:",
        stopWatch.ElapsedMilliseconds,
        ConsoleColor.Green);
    basicMaintainer.WaitForBackgroundThreads();
  }

  private static IZoneTree<int, int> OpenOrCreateZoneTree(WriteAheadLogMode mode, string dataPath)
  {
    return new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetMutableSegmentMaxItemCount(TestConfig.MutableSegmentMaxItemCount)
        .SetDiskSegmentCompressionBlockSize(TestConfig.DiskCompressionBlockSize)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = TestConfig.DiskSegmentMode)
        .ConfigureWriteAheadLogOptions(x =>
        {
          x.CompressionBlockSize = TestConfig.WALCompressionBlockSize;
          x.WriteAheadLogMode = mode;
        })
        .OpenOrCreate();
  }
}
