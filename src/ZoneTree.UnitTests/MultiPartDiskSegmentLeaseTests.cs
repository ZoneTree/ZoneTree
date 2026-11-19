using ZoneTree.AbstractFileStream;
using ZoneTree.Collections;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Logger;
using ZoneTree.Hashers;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Segments.Disk;
using ZoneTree.Segments.MultiPart;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class MultiPartDiskSegmentLeaseTests
{
  [Test]
  public void DeferredDropPreservesTheFirstExclusionPlan()
  {
    var options = CreateOptions();
    var deviceManager = options.RandomAccessDeviceManager;
    var idProvider = new IncrementalIdProvider();
    IDiskSegment<int, int> preservedPart = null;
    IDiskSegment<int, int> droppedPart = null;
    IDiskSegment<int, int> multiPart = null;
    var iteratorAttached = false;

    try
    {
      preservedPart = CreatePart(options, idProvider, 1, 2);
      droppedPart = CreatePart(options, idProvider, 3, 4);
      using (var creator = new MultiPartDiskSegmentCreator<int, int>(
          options,
          idProvider))
      {
        creator.Append(preservedPart, 1, 2, 1, 2);
        creator.Append(droppedPart, 3, 4, 3, 4);
        multiPart = creator.CreateReadOnlyDiskSegment();
      }

      var excludedPartIds = new HashSet<long> { preservedPart.SegmentId };
      multiPart.AttachIterator();
      iteratorAttached = true;

      multiPart.Drop(excludedPartIds);
      excludedPartIds.Clear();
      multiPart.Drop();

      Assert.Multiple(() =>
      {
        Assert.That(DeviceExists(deviceManager, preservedPart), Is.True);
        Assert.That(DeviceExists(deviceManager, droppedPart), Is.True);
        Assert.That(MultiPartMetadataExists(deviceManager, multiPart), Is.True);
      });

      multiPart.DetachIterator();
      iteratorAttached = false;

      Assert.Multiple(() =>
      {
        Assert.That(DeviceExists(deviceManager, preservedPart), Is.True);
        Assert.That(DeviceExists(deviceManager, droppedPart), Is.False);
        Assert.That(MultiPartMetadataExists(deviceManager, multiPart), Is.False);
        var keyHashProvider = new KeyHashProvider<int>();
        Assert.That(preservedPart.TryGet(1, out var value, ref keyHashProvider), Is.True);
        Assert.That(value, Is.EqualTo(1));
      });
    }
    finally
    {
      if (iteratorAttached)
        multiPart.DetachIterator();
      multiPart?.Drop();
      preservedPart?.Drop();
      droppedPart?.Drop();
      deviceManager.DropStore();
    }
  }

  static ZoneTreeOptions<int, int> CreateOptions()
  {
    var logger = new ConsoleLogger(LogLevel.Error);
    var options = new ZoneTreeOptions<int, int>
    {
      Comparer = new Int32ComparerAscending(),
      KeyHasher = new DefaultKeyHasher<int>(),
      KeySerializer = new Int32Serializer(),
      ValueSerializer = new Int32Serializer(),
      Logger = logger,
      WriteAheadLogProvider = new NullWriteAheadLogProvider(),
      RandomAccessDeviceManager = new RandomAccessDeviceManager(
          logger,
          new InMemoryFileStreamProvider(),
          "data/DeferredDropPreservesTheFirstExclusionPlan"),
      DiskSegmentOptions = new DiskSegmentOptions
      {
        Mode = DiskSegmentMode.MultiPartDiskSegment,
        CompressionMethod = CompressionMethod.None,
        CompressionLevel = 0,
        CompressionBlockSize = 1024,
        MinimumRecordCount = 1,
        MaximumRecordCount = 100,
        DefaultSparseArrayStepSize = 0,
      },
      AllowUnsafeOptionValues = true,
    };
    options.DisableDeletion();
    options.Validate();
    return options;
  }

  static IDiskSegment<int, int> CreatePart(
      ZoneTreeOptions<int, int> options,
      IIncrementalIdProvider idProvider,
      params int[] keys)
  {
    using var creator = new DiskSegmentCreator<int, int>(options, idProvider);
    foreach (var key in keys)
      creator.Append(key, key, IteratorPosition.None);
    return creator.CreateReadOnlyDiskSegment();
  }

  static bool DeviceExists(
      IRandomAccessDeviceManager deviceManager,
      IDiskSegment<int, int> segment)
  {
    return deviceManager.DeviceExists(
        segment.SegmentId,
        DiskSegmentConstants.DataCategory,
        isCompressed: true);
  }

  static bool MultiPartMetadataExists(
      IRandomAccessDeviceManager deviceManager,
      IDiskSegment<int, int> segment)
  {
    return deviceManager.DeviceExists(
        segment.SegmentId,
        DiskSegmentConstants.MultiPartDiskSegmentCategory,
        isCompressed: false);
  }
}
