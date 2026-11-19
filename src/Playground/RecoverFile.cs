using System.Diagnostics;
using ZoneTree;
using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments.Disk;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace Playground;

public sealed class RecoverFile
{
  public static void Recover1()
  {
    var path = @"..\..\data\AsyncPrivate-50M-str-str";
    var fileStreamProvider = new LocalFileStreamProvider();
    var logger = new ConsoleLogger();
    var deviceManager = new RandomAccessDeviceManager(logger, fileStreamProvider, path);
    ZoneTreeMetaWAL<string, string>.LoadZoneTreeMetaWithoutWALRecords(deviceManager);
    var options = new ZoneTreeOptions<string, string>
    {
      Logger = logger,
      WriteAheadLogProvider = new WriteAheadLogProvider(
            new ConsoleLogger(), fileStreamProvider, path),
      RandomAccessDeviceManager = deviceManager,
      DiskSegmentOptions = new()
      {
      },
      KeySerializer = new Utf8StringSerializer(),
      ValueSerializer = new Utf8StringSerializer(),
      Comparer = new StringOrdinalComparerAscending()
    };
    options.WriteAheadLogOptions.WriteAheadLogMode
        = WriteAheadLogMode.AsyncPrivate;

    var stopWatch = new Stopwatch();
    var disk = DiskSegmentFactory.CreateDiskSegment(54, options);
    disk.InitSparseArray(100);

    Console.WriteLine("Elapsed: " + stopWatch.ElapsedMilliseconds);
  }

  public static void Recover2()
  {
    var path = @"..\..\data\Private-3M-transactional-int-int";
    var logger = new ConsoleLogger();
    var fileStreamProvider = new LocalFileStreamProvider();
    var deviceManager = new RandomAccessDeviceManager(logger, fileStreamProvider, path);
    var meta = ZoneTreeMetaWAL<int, int>.LoadZoneTreeMetaWithoutWALRecords(deviceManager);
    var options = new ZoneTreeOptions<int, int>
    {
      Logger = logger,
      WriteAheadLogProvider = new WriteAheadLogProvider(
            new ConsoleLogger(), fileStreamProvider, path),
      WriteAheadLogOptions = meta.WriteAheadLogOptions,
      RandomAccessDeviceManager = deviceManager,
      DiskSegmentOptions = new()
      {
        CompressionBlockSize = meta.DiskSegmentOptions.CompressionBlockSize,
      },
      KeySerializer = new Int32Serializer(),
      ValueSerializer = new Int32Serializer(),
      Comparer = new Int32ComparerAscending()
    };
    new ZoneTreeFactory<int, int>().SetOptions(options).Open();
    var stopWatch = new Stopwatch();
    Console.WriteLine("Elapsed: " + stopWatch.ElapsedMilliseconds);
  }
}
