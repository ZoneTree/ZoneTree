using Humanizer;
using System.Diagnostics;
using ZoneTree;
using ZoneTree.Core;
using ZoneTree.Options;

namespace Playground.Benchmark;

/// <summary>
/// https://www.linkedin.com/feed/update/urn:li:activity:6969335455733452800?commentUrn=urn%3Ali%3Acomment%3A%28activity%3A6969335455733452800%2C6971528988607279104%29
/// How about a billion variable-length records averaging 8-byte keys and 8-byte values?
/// That's a little more challenging. 😀
/// </summary>
public sealed class StevesChallenge : ZoneTreeTestBase<Memory<byte>, Memory<byte>>
{
  const string FolderName = "-byte-byte";

  public override string DataPath =>
      RootPath + WalMode + "-" + Count.ToHuman()
          + "_" + CompressionMethod + "_"
          + FolderName;

  volatile int Throttle = 0;

  public void Insert(IStatsCollector stats)
  {
    stats.Name = "Insert";
    AddOptions(stats);
    var count = Count;
    stats.LogWithColor(GetLabel("Insert <byte-byte>"), ConsoleColor.Cyan);

    if (TestConfig.RecreateDatabases && Directory.Exists(DataPath))
      Directory.Delete(DataPath, true);

    stats.RestartStopwatch();

    using var zoneTree = OpenOrCreateZoneTree();
    using var maintainer = CreateMaintainer(zoneTree);
    stats.AddStage("Loaded In");
    var random = new Random(0);

    var cts = new CancellationTokenSource();
    Task.Factory.StartNew(async () =>
    {
      using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
      while (await timer.WaitForNextTickAsync(cts.Token))
      {
        if (cts.IsCancellationRequested)
          break;
        if (zoneTree.Maintenance.ReadOnlySegmentsCount > 60)
          Throttle = 100;
        else
          Throttle = 0;
      }
    });
    for (var x = 0; x < count; ++x)
    {
      if (Throttle > 0)
        Thread.Sleep(Throttle);
      var bytes = new byte[random.Next(1, 16)];
      random.NextBytes(bytes);
      zoneTree.Upsert(bytes, bytes);
      if (x > 0 && x % 10_000_000 == 0)
        stats.AddStage(x / 10_000_000 + " x 10M", ConsoleColor.DarkGray, false);
    }
    stats.AddStage("Inserted In", ConsoleColor.Green);
    cts.Cancel();

    if (WalMode == WriteAheadLogMode.None)
    {
      zoneTree.Maintenance.MoveMutableSegmentForward();
      zoneTree.Maintenance.StartMergeOperation()?.Join();
    }
    maintainer.WaitForBackgroundThreads();

    stats.AddStage("Merged In", ConsoleColor.DarkCyan);
  }

  public void Iterate(IStatsCollector stats)
  {
    stats.Name = "Iterate";
    var count = Count;
    stats.LogWithColor(GetLabel("Iterate <byte-byte>"), ConsoleColor.Cyan);
    stats.RestartStopwatch();

    using var zoneTree = OpenOrCreateZoneTree();
    using var maintainer = CreateMaintainer(zoneTree);

    stats.AddStage("Loaded in", ConsoleColor.DarkYellow);

    var off = 0;
    using var iterator = zoneTree.CreateIterator();
    while (iterator.Next())
    {
      ++off;
    }
    if (off != count)
      Console.WriteLine($"total records. {off}");

    stats.AddStage(
        "Iterated in",
        ConsoleColor.Green);
    maintainer.WaitForBackgroundThreads();
  }

}
