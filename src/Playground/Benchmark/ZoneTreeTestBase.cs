using Humanizer;
using ZoneTree;
using ZoneTree.Options;

namespace Playground.Benchmark;

public class ZoneTreeTestBase<TKey, TValue>
{
  public virtual string RootPath { get; set; } = "../../data/";

  public virtual string DataPath => "base";

  public WriteAheadLogMode WALMode { get; set; } = WriteAheadLogMode.None;

  public int Count = 1_000_000;

  public CompressionMethod CompressionMethod { get; set; } = CompressionMethod.LZ4;

  public int CompressionLevel { get; set; } = CompressionLevels.LZ4Fastest;

  protected string GetLabel(string label)
  {
    return $"{WALMode} {label}" +
        $" {Count.ToHuman()} - "
        + CompressionMethod;
  }

  public int MergeFrom { get; set; }

  public int MergeTo { get; set; }

  protected ZoneTreeFactory<TKey, TValue> GetFactory()
  {
    return new ZoneTreeFactory<TKey, TValue>()
        .DisableDeletion()
        .SetMutableSegmentMaxItemCount(TestConfig.MutableSegmentMaxItemCount)
        .SetDiskSegmentMaxItemCount(TestConfig.DiskSegmentMaxItemCount)
        .SetDiskSegmentCompressionBlockSize(TestConfig.DiskCompressionBlockSize)
        .SetDataDirectory(DataPath)
        .ConfigureDiskSegmentOptions(x =>
        {
          x.DiskSegmentMode = TestConfig.DiskSegmentMode;
          x.CompressionMethod = CompressionMethod;
          x.CompressionLevel = TestConfig.CompressionLevel;
        })
        .ConfigureWriteAheadLogOptions(x =>
        {
          x.CompressionMethod = CompressionMethod;
          x.CompressionLevel = TestConfig.CompressionLevel;
          x.CompressionBlockSize = TestConfig.WALCompressionBlockSize;
          x.WriteAheadLogMode = WALMode;
        });
  }

  protected IZoneTree<TKey, TValue> OpenOrCreateZoneTree()
  {
    return GetFactory().OpenOrCreate();
  }

  protected ITransactionalZoneTree<TKey, TValue> OpenOrCreateTransactionalZoneTree()
  {
    return GetFactory().OpenOrCreateTransactional();
  }

  protected IMaintainer CreateMaintainer(IZoneTree<TKey, TValue> zoneTree)
  {
    var maintainer = zoneTree.CreateMaintainer();
    maintainer.ThresholdForMergeOperationStart = TestConfig.ThresholdForMergeOperationStart;
    return maintainer;
  }

  protected void AddOptions(IStatsCollector stats)
  {
    stats.SetOption("WAL", WALMode);
    stats.SetOption("Compression", CompressionMethod);
    stats.SetOption("Count", Count);
  }

  public void AddDatabaseFileUsage(IStatsCollector stats)
  {
    var path = DataPath;
    var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
    long totalBytes = 0;
    foreach (var file in files)
    {
      var finfo = new FileInfo(file);
      totalBytes += finfo.Length;
    }
    stats.AddAdditionalStats("Disk Usage", totalBytes.Bytes().Humanize());
  }

  static void PrintBottomSegments(IZoneTree<TKey, TValue> z)
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

  public void MergeBottomSegments(IStatsCollector stats)
  {
    stats.Name = "Merge Bottom Segments";
    stats.LogWithColor(GetLabel($"Merge Bottom from: {MergeFrom} to: {MergeTo}"), ConsoleColor.Cyan);
    stats.RestartStopwatch();

    using var zoneTree = OpenOrCreateZoneTree();
    stats.AddStage("Loaded in", ConsoleColor.DarkYellow);
    zoneTree.Maintenance.StartBottomSegmentsMergeOperation(MergeFrom, MergeTo).Join();
    PrintBottomSegments(zoneTree);
    stats.AddStage("Merged in", ConsoleColor.Green);
  }

  public void ShowBottomSegments(IStatsCollector stats)
  {
    stats.Name = "Show Bottom Segments";
    stats.LogWithColor(GetLabel("Show Bottom Segments"), ConsoleColor.Cyan);
    stats.RestartStopwatch();

    using var zoneTree = OpenOrCreateZoneTree();
    stats.AddStage("Loaded in", ConsoleColor.DarkYellow);
    PrintBottomSegments(zoneTree);
    stats.AddStage("Showed in", ConsoleColor.Green);
  }
}
