using System.Reflection;
using ZoneTree.AbstractFileStream;
using ZoneTree.Collections;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Hashers;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Segments.Disk;
using ZoneTree.Segments.InMemory;
using ZoneTree.Segments.MultiPart;
using ZoneTree.Segments.NullDisk;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class BottomSegmentMergeTests
{
  [Test]
  public void IntIntBottomMerge()
  {
    var dataPath = "data/IntIntBottomMerge";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    var zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDiskSegmentMaxItemCount(10)
        .SetDataDirectory(dataPath)
        .ConfigureDiskSegmentOptions(
            x => x.DiskSegmentMode = DiskSegmentMode.SingleDiskSegment)
        .OpenOrCreate();
    var m = zoneTree.Maintenance;
    var insertCount = 100;
    var p = 10;
    for (var i = 0; i < insertCount; ++i)
    {
      zoneTree.Upsert(i, i + i);
      if (i % p == p - 1)
      {
        m.MoveMutableSegmentForward();
        m.StartMergeOperation().Join();
        for (var j = 0; j < p - 10; ++j)
        {
          ++i;
          if (i >= insertCount)
            break;
          zoneTree.Upsert(i, i + i);
        }
        ++p;
      }
    }

    var expected1 = new long[] { 21, 19, 17, 15, 13, 11 };
    var expected2 = new long[] { 21, 51, 13, 11 };

    var sum =
        m.BottomSegments.Sum(x => x.Length) +
        m.InMemoryRecordCount +
        m.DiskSegment.Length;
    var lens = m.BottomSegments.Select(x => x.Length).ToArray();
    Assert.That(sum, Is.EqualTo(insertCount));
    Assert.That(lens, Is.EqualTo(expected1));
    m.StartBottomSegmentsMergeOperation(1, 3).Join();

    lens = m.BottomSegments.Select(x => x.Length).ToArray();
    Assert.That(lens, Is.EqualTo(expected2));
    zoneTree.Dispose();

    zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDiskSegmentMaxItemCount(10)
        .SetDataDirectory(dataPath)
        .OpenOrCreate();

    lens = m.BottomSegments.Select(x => x.Length).ToArray();
    Assert.That(lens, Is.EqualTo(expected2));
    zoneTree.Maintenance.Drop();
    zoneTree.Dispose();
  }

  [Test]
  public void PartialMultiPartBottomMergeCarriesPartsFromMergedBottomSegment()
  {
    var options = CreateMultiPartBottomMergeOptions();
    var idProvider = new IncrementalIdProvider();

    var untouchedBottomSegment = CreateMultiPartSegment(
        options,
        idProvider,
        CreateRange(200, 10));
    var firstMergedBottomSegment = CreateMultiPartSegment(
        options,
        idProvider,
        CreateRange(0, 10),
        CreateRange(100, 10));
    var lastMergedBottomSegment = CreateMultiPartSegment(
        options,
        idProvider,
        CreateRange(50, 10),
        CreateRange(150, 10));
    var mutableSegment = new MutableSegment<int, int>(
        options,
        idProvider.NextId(),
        new IncrementalIdProvider());
    var zoneTree = new ZoneTree<int, int>(
        options,
        new ZoneTreeMeta(),
        Array.Empty<IReadOnlySegment<int, int>>(),
        mutableSegment,
        new NullDiskSegment<int, int>(),
        [
            untouchedBottomSegment,
                firstMergedBottomSegment,
                lastMergedBottomSegment
        ],
        idProvider.LastId);

    try
    {
      zoneTree.Maintenance.StartBottomSegmentsMergeOperation(1, 2).Join();

      foreach (var key in ExpectedKeysAfterPartialMultiPartMerge())
      {
        Assert.That(
            zoneTree.TryGet(key, out var value),
            Is.True,
            $"Key {key} was lost after partial multipart bottom merge.");
        Assert.That(value, Is.EqualTo(key));
      }
    }
    finally
    {
      zoneTree.Dispose();
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  [Test]
  public void BottomMergeDoesNotApplySnapshotResultToChangedBottomIndexes()
  {
    var options = CreateSingleDiskBottomMergeOptions();
    var idProvider = new IncrementalIdProvider();

    var segmentAtOriginalIndexZero = CreatePartSegment(
        options,
        idProvider,
        CreateRange(0, 10));
    var firstMergedBottomSegment = CreatePartSegment(
        options,
        idProvider,
        CreateRange(100, 10));
    var lastMergedBottomSegment = CreatePartSegment(
        options,
        idProvider,
        CreateRange(200, 10));
    var concurrentlyAddedBottomSegment = CreatePartSegment(
        options,
        idProvider,
        CreateRange(300, 10));
    var mutableSegment = new MutableSegment<int, int>(
        options,
        idProvider.NextId(),
        new IncrementalIdProvider());
    var zoneTree = new ZoneTree<int, int>(
        options,
        new ZoneTreeMeta(),
        Array.Empty<IReadOnlySegment<int, int>>(),
        mutableSegment,
        new NullDiskSegment<int, int>(),
        [
            segmentAtOriginalIndexZero,
            firstMergedBottomSegment,
            lastMergedBottomSegment
        ],
        idProvider.LastId);

    var injected = false;
    void InjectNewestBottomSegment(
        IZoneTreeMaintenance<int, int> _,
        IDiskSegment<int, int> __,
        bool isBottomSegment)
    {
      if (!isBottomSegment || injected)
        return;

      injected = true;
      InjectNewestBottomSegmentIntoQueue(zoneTree, concurrentlyAddedBottomSegment);
    }

    zoneTree.Maintenance.OnDiskSegmentCreated += InjectNewestBottomSegment;
    try
    {
      zoneTree.Maintenance.StartBottomSegmentsMergeOperation(1, 2).Join();

      Assert.That(injected, Is.True);
      foreach (var key in ExpectedKeysAfterConcurrentBottomSegmentAdd())
      {
        Assert.That(
            zoneTree.TryGet(key, out var value),
            Is.True,
            $"Key {key} was lost after bottom merge committed against changed indexes.");
        Assert.That(value, Is.EqualTo(key));
      }
    }
    finally
    {
      zoneTree.Maintenance.OnDiskSegmentCreated -= InjectNewestBottomSegment;
      zoneTree.Dispose();
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  [Test]
  public void BottomMergeKeepsTombstoneWhenOlderBottomSegmentRemains()
  {
    var options = CreateSingleDiskBottomMergeOptionsWithDeletion();
    var idProvider = new IncrementalIdProvider();

    var deleteBottomSegment = CreatePartSegmentFromEntries(
        options,
        idProvider,
        (42, -1));
    var firstMergedBottomSegment = CreatePartSegmentFromEntries(
        options,
        idProvider,
        (50, 50));
    var olderBottomSegment = CreatePartSegmentFromEntries(
        options,
        idProvider,
        (42, 420),
        (60, 60));
    var mutableSegment = new MutableSegment<int, int>(
        options,
        idProvider.NextId(),
        new IncrementalIdProvider());
    var zoneTree = new ZoneTree<int, int>(
        options,
        new ZoneTreeMeta(),
        Array.Empty<IReadOnlySegment<int, int>>(),
        mutableSegment,
        new NullDiskSegment<int, int>(),
        [
            deleteBottomSegment,
            firstMergedBottomSegment,
            olderBottomSegment
        ],
        idProvider.LastId);

    try
    {
      zoneTree.Maintenance.StartBottomSegmentsMergeOperation(0, 1).Join();

      Assert.That(
          zoneTree.TryGet(42, out _),
          Is.False,
          "The tombstone for key 42 was dropped while an older bottom segment still had the key.");
      Assert.That(zoneTree.TryGet(50, out var value50), Is.True);
      Assert.That(value50, Is.EqualTo(50));
      Assert.That(zoneTree.TryGet(60, out var value60), Is.True);
      Assert.That(value60, Is.EqualTo(60));
    }
    finally
    {
      zoneTree.Dispose();
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  [Test]
  public void MultiPartCreatorDropBeforeHeaderIsWrittenDropsPendingPart()
  {
    var options = CreateMultiPartBottomMergeOptions(
        "data/MultiPartCreatorDropBeforeHeaderIsWrittenDropsPendingPart");
    var idProvider = new IncrementalIdProvider();
    try
    {
      using var creator = new MultiPartDiskSegmentCreator<int, int>(
          options,
          idProvider);

      creator.Append(1, 1, IteratorPosition.None);

      Assert.That(
          options.RandomAccessDeviceManager.DeviceExists(
              2,
              DiskSegmentConstants.DataCategory,
              isCompressed: true),
          Is.True);

      Assert.DoesNotThrow(() => creator.DropDiskSegment());

      Assert.That(
          options.RandomAccessDeviceManager.DeviceExists(
              1,
              DiskSegmentConstants.MultiPartDiskSegmentCategory,
              isCompressed: false),
          Is.False);
      Assert.That(
          options.RandomAccessDeviceManager.DeviceExists(
              2,
              DiskSegmentConstants.DataCategory,
              isCompressed: true),
          Is.False);
      Assert.That(options.RandomAccessDeviceManager.DeviceCount, Is.EqualTo(0));
    }
    finally
    {
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  static ZoneTreeOptions<int, int> CreateMultiPartBottomMergeOptions(
      string dataDirectory = "data/PartialMultiPartBottomMerge")
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
            dataDirectory),
      DiskSegmentOptions = new DiskSegmentOptions
      {
        DiskSegmentMode = DiskSegmentMode.MultiPartDiskSegment,
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

  static ZoneTreeOptions<int, int> CreateSingleDiskBottomMergeOptionsWithDeletion()
  {
    var options = CreateSingleDiskBottomMergeOptions();
    options.IsDeleted = (in int _, in int value) => value == -1;
    options.MarkValueDeleted = (ref int value) => value = -1;
    options.Validate();
    return options;
  }

  static ZoneTreeOptions<int, int> CreateSingleDiskBottomMergeOptions()
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
            "data/ConcurrentBottomSegmentAddDuringBottomMerge"),
      DiskSegmentOptions = new DiskSegmentOptions
      {
        DiskSegmentMode = DiskSegmentMode.SingleDiskSegment,
        CompressionMethod = CompressionMethod.None,
        CompressionLevel = 0,
        CompressionBlockSize = 1024,
        DefaultSparseArrayStepSize = 0,
      },
      AllowUnsafeOptionValues = true,
    };
    options.DisableDeletion();
    options.Validate();
    return options;
  }

  static IDiskSegment<int, int> CreateMultiPartSegment(
      ZoneTreeOptions<int, int> options,
      IIncrementalIdProvider idProvider,
      params int[][] parts)
  {
    using IDiskSegmentCreator<int, int> creator =
        new MultiPartDiskSegmentCreator<int, int>(options, idProvider);
    foreach (var keys in parts)
    {
      var part = CreatePartSegment(options, idProvider, keys);
      creator.Append(
          part,
          keys[0],
          keys[^1],
          keys[0],
          keys[^1]);
    }
    return creator.CreateReadOnlyDiskSegment();
  }

  static IDiskSegment<int, int> CreatePartSegment(
      ZoneTreeOptions<int, int> options,
      IIncrementalIdProvider idProvider,
      IReadOnlyList<int> keys)
  {
    using var creator = new DiskSegmentCreator<int, int>(options, idProvider);
    foreach (var key in keys)
    {
      creator.Append(key, key, IteratorPosition.None);
    }
    return creator.CreateReadOnlyDiskSegment();
  }

  static IDiskSegment<int, int> CreatePartSegmentFromEntries(
      ZoneTreeOptions<int, int> options,
      IIncrementalIdProvider idProvider,
      params (int Key, int Value)[] entries)
  {
    using var creator = new DiskSegmentCreator<int, int>(options, idProvider);
    foreach (var entry in entries)
    {
      creator.Append(entry.Key, entry.Value, IteratorPosition.None);
    }
    return creator.CreateReadOnlyDiskSegment();
  }

  static int[] CreateRange(int start, int count) =>
      Enumerable.Range(start, count).ToArray();

  static void InjectNewestBottomSegmentIntoQueue(
      ZoneTree<int, int> zoneTree,
      IDiskSegment<int, int> bottomSegment)
  {
    var bottomSegments = zoneTree.Maintenance.BottomSegments;
    var newBottomSegments = new[] { bottomSegment }
        .Concat(bottomSegments)
        .ToArray();
    var newQueue =
        new SingleProducerSingleConsumerQueue<IDiskSegment<int, int>>(Enumerable.Reverse(newBottomSegments));
    var field = typeof(ZoneTree<int, int>).GetField(
        "BottomSegmentQueue",
        BindingFlags.Instance | BindingFlags.NonPublic);
    field.SetValue(zoneTree, newQueue);
  }

  static IEnumerable<int> ExpectedKeysAfterPartialMultiPartMerge() =>
      Enumerable.Range(0, 10)
          .Concat(Enumerable.Range(50, 10))
          .Concat(Enumerable.Range(100, 10))
          .Concat(Enumerable.Range(150, 10))
          .Concat(Enumerable.Range(200, 10));

  static IEnumerable<int> ExpectedKeysAfterConcurrentBottomSegmentAdd() =>
      Enumerable.Range(0, 10)
          .Concat(Enumerable.Range(100, 10))
          .Concat(Enumerable.Range(200, 10))
          .Concat(Enumerable.Range(300, 10));
}
