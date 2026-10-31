using Playground;
using Playground.Benchmark;
using ZoneTree.Logger;
using ZoneTree.Options;

TestConfig.MutableSegmentMaxItemCount = 1_000_000;
TestConfig.ThresholdForMergeOperationStart = 2_000_000;
TestConfig.RecreateDatabases = true;
TestConfig.EnableParalelInserts = false;
TestConfig.DiskCompressionBlockSize = 1024 * 1024 * 10;
TestConfig.WALCompressionBlockSize = 1024 * 32 * 8;
TestConfig.DiskSegmentMode = DiskSegmentMode.SingleDiskSegment;
ConsoleLogger.DefaultLogLevel = LogLevel.Info;

TestConfig.PrintConfig();

var testCase = 5;

if (testCase == 1)
{
  // bottom segment merger tests.
  var c = 1_000_000_000;
  OldTests.ShowBottomSegments(WriteAheadLogMode.None, c);
  //OldTests.MergeBottomSegment(WriteAheadLogMode.None, c, 3, 6);
  //OldTests.Iterate(WriteAheadLogMode.None, c);
}

if (testCase == 2)
{
  // multiple iterator tests.
  var c = 1_000_000;
  var ic = 1000;
  OldTests.Insert(WriteAheadLogMode.None, c);
  OldTests.MultipleIterate(WriteAheadLogMode.None, c, ic);
}

if (testCase == 3)
{
  var b = new Benchmark();
  var test1 = new ZoneTreeTest1();
  var test2 = new ZoneTreeTest2
  {
    WALMode = test1.WALMode = WriteAheadLogMode.AsyncCompressed
  };
  var methods = new (CompressionMethod method, int level)[]
  {
        (CompressionMethod.LZ4, CompressionLevels.LZ4Fastest),
        (CompressionMethod.Zstd, CompressionLevels.Zstd0),
        (CompressionMethod.Brotli, CompressionLevels.BrotliFastest),
        (CompressionMethod.Gzip, CompressionLevels.GzipFastest),
        (CompressionMethod.None, 0),
  };

  test2.Count = test1.Count = 10_000_000;
  test2.WALMode = test1.WALMode = WriteAheadLogMode.AsyncCompressed;

  b.NewSection("int-int insert");
  foreach (var (method, level) in methods)
  {
    test1.CompressionMethod = method;
    test1.CompressionLevel = level;
    var stats = b.Run(test1.Insert);
    test1.AddDatabaseFileUsage(stats);
  }

  b.NewSection("str-str insert");
  foreach (var (method, level) in methods)
  {
    test2.CompressionMethod = method;
    test2.CompressionLevel = level;
    var stats = b.Run(test2.Insert);
    test2.AddDatabaseFileUsage(stats);
  }

  b.NewSection("int-int iterate");
  foreach (var (method, level) in methods)
  {
    test1.CompressionMethod = method;
    test1.CompressionLevel = level;
    b.Run(test1.Iterate);
  }

  b.NewSection("str-str iterate");
  foreach (var (method, level) in methods)
  {
    test2.CompressionMethod = method;
    test2.CompressionLevel = level;
    b.Run(test2.Iterate);
  }

  File.WriteAllText(@"..\..\data\benchmark.json", b.ToJSON());
}

if (testCase == 4)
{
  TestConfig.ThresholdForMergeOperationStart = 20_000_000;
  TestConfig.DiskSegmentMaxItemCount = 5_000_000;
  TestConfig.DiskCompressionBlockSize = 1024 * 1024 * 100;
  var b = new Benchmark();
  var steve = new StevesChallenge
  {
    Count = 1_000_000_000,
    WALMode = WriteAheadLogMode.None,
    MergeFrom = 0,
    MergeTo = 100
  };
  b.Run(steve.Insert);
  b.Run(steve.Iterate);
  b.Run(steve.ShowBottomSegments);
  b.Run(steve.MergeBottomSegments);
}

if (testCase == 5)
{
  new DeadlockFinder().RunDeadLockFinder();
}
