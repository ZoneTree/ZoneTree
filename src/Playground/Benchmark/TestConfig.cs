using ZoneTree.Core;
using ZoneTree.Options;

namespace Playground.Benchmark;

public static class TestConfig
{
  public static bool RecreateDatabases = true;

  public static int ThresholdForMergeOperationStart = 2_000_000;

  public static int MutableSegmentMaxItemCount = 1_000_000;

  public static int DiskSegmentMaxItemCount = 20_000_000;

  public static int WALCompressionBlockSize = 32768;

  public static int DiskCompressionBlockSize = 32768;

  public static bool EnableParalelInserts = false;

  public static DiskSegmentMode DiskSegmentMode = DiskSegmentMode.MultiPartDiskSegment;

  public static CompressionMethod CompressionMethod = CompressionMethod.LZ4;

  public static int CompressionLevel = 0;

  public static void PrintConfig()
  {
    Console.WriteLine($"ThresholdForMergeOperationStart: {ThresholdForMergeOperationStart}");
    Console.WriteLine($"MutableSegmentMaxItemCount: {MutableSegmentMaxItemCount}");
    Console.WriteLine($"DiskSegmentMaxItemCount: {DiskSegmentMaxItemCount}");
    Console.WriteLine($"WALCompressionBlockSize: {WALCompressionBlockSize}");
    Console.WriteLine($"DiskCompressionBlockSize: {DiskCompressionBlockSize}");
    Console.WriteLine($"EnableParalelInserts: {EnableParalelInserts}");
    Console.WriteLine($"DiskSegmentMode: {DiskSegmentMode}");
    Console.WriteLine($"CompressionMethod: {CompressionMethod}");
    Console.WriteLine($"CompressionLevel: {CompressionLevel}");
  }
}
