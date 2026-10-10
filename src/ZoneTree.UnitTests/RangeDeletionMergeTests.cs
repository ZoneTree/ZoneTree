using ZoneTree.AbstractFileStream;
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

public sealed class RangeDeletionMergeTests
{
  [TestCase(true, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(false, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(null, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(true, DiskSegmentMode.SingleDiskSegment)]
  public void NormalMergeRemovesExpiredPartsAndPreservesNewerOverlappingValues(
      bool? rangeResult, DiskSegmentMode outputMode)
  {
    var values = new TrackingValueSerializer();
    var options = CreateOptions(values);
    options.DiskSegmentOptions.DiskSegmentMode = outputMode;
    var ranges = new List<(int, int, int, int)>();
    if (rangeResult.HasValue)
      options.IsRangeDeleted = (in int firstKey, in int firstValue, in int lastKey, in int lastValue) =>
      {
        ranges.Add((firstKey, firstValue, lastKey, lastValue));
        return rangeResult.Value && lastValue < 1008;
      };
    var ids = new IncrementalIdProvider();
    var disk = CreateSegment(options, ids, [0, 1, 2, 3], [4], [5, 6, 7], [10, 11, 12]);
    var expiredParts = Enumerable.Range(0, 3).Select(disk.GetPart).ToArray();
    using var tree = CreateTree(options, ids, disk);
    try
    {
      tree.Upsert(0, 2000);
      tree.Upsert(2, 2002);
      tree.Upsert(11, 2011);
      tree.Upsert(100, 2100);
      tree.MoveMutableSegmentForward();
      values.Reads.Clear();
      MergeResult? result = null;
      tree.OnMergeOperationEnded += (_, status) => result = status;
      tree.StartMergeOperation().Join();

      Assert.That(result, Is.EqualTo(MergeResult.SUCCESS));
      Assert.That(tree.DiskSegment.Length, Is.EqualTo(6));
      if (rangeResult == true)
      {
        Assert.That(ranges, Does.Contain((0, 1000, 3, 1003)));
        Assert.That(ranges, Does.Contain((4, 1004, 4, 1004)));
        Assert.That(ranges, Does.Contain((5, 1005, 7, 1007)));
        Assert.That(values.Reads.Where(x => x >= 1000 && x < 1008), Is.Empty,
            "Expired part payloads must not be deserialized during the merge.");
      }
      foreach (var part in expiredParts)
        Assert.That(DataExists(options, part), Is.False);
      foreach (var key in new[] { 0, 2, 10, 11, 12, 100 })
        Assert.That(tree.TryGet(key, out _), Is.True);
      Assert.That(tree.TryGet(0, out var value0), Is.True);
      Assert.That(value0, Is.EqualTo(2000));
      Assert.That(tree.TryGet(2, out var value2), Is.True);
      Assert.That(value2, Is.EqualTo(2002));
      Assert.That(tree.TryGet(11, out var value11), Is.True);
      Assert.That(value11, Is.EqualTo(2011));
      foreach (var key in new[] { 1, 3, 4, 5, 6, 7 })
        Assert.That(tree.TryGet(key, out _), Is.False);
    }
    finally
    {
      tree.Dispose();
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  [TestCase(false)]
  [TestCase(true)]
  public void MergeCanRemoveEveryPart(bool bottomMerge)
  {
    var values = new TrackingValueSerializer();
    var options = CreateOptions(values);
    options.IsRangeDeleted = (in int _, in int _, in int _, in int _) => true;
    var ids = new IncrementalIdProvider();
    var expired = CreateSegment(options, ids, [0, 1, 2], [3], [4, 5, 6, 7]);
    using var tree = CreateTree(options, ids,
        bottomMerge ? new NullDiskSegment<int, int>() : expired,
        bottomMerge ? [expired] : []);
    try
    {
      MergeResult? result = null;
      values.Reads.Clear();
      if (bottomMerge)
      {
        tree.OnBottomSegmentsMergeOperationEnded += (_, status) => result = status;
        tree.StartBottomSegmentsMergeOperation(0, int.MaxValue).Join();
      }
      else
      {
        tree.Upsert(100, -1);
        tree.MoveMutableSegmentForward();
        tree.OnMergeOperationEnded += (_, status) => result = status;
        tree.StartMergeOperation().Join();
      }
      Assert.That(result, Is.EqualTo(MergeResult.SUCCESS));
      Assert.That(tree.TotalRecordCount, Is.Zero);
      Assert.That(values.Reads, Is.Empty);
    }
    finally
    {
      tree.Dispose();
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  [TestCase(false)]
  [TestCase(true)]
  public void MergeKeepsTombstonesWhenOlderBottomSegmentRemains(bool bottomMerge)
  {
    var options = CreateOptions(new TrackingValueSerializer());
    var calls = 0;
    options.IsRangeDeleted = (in int _, in int _, in int _, in int _) =>
    {
      ++calls;
      return true;
    };
    var ids = new IncrementalIdProvider();
    var tombstones = CreateSegment(options, ids, [0, 1, 2]);
    var older = CreateSegment(options, ids, key => key + 2000, [0, 1, 2]);
    using var tree = CreateTree(options, ids,
        bottomMerge ? new NullDiskSegment<int, int>() : tombstones,
        bottomMerge ? [tombstones, older] : [older]);
    try
    {
      MergeResult? result = null;
      if (bottomMerge)
      {
        // The upper index is clamped to the available inputs. Add another
        // selected input so that this is a partial bottom merge.
        tree.Upsert(100, 2100);
        options.DiskSegmentMaxItemCount = 0;
        tree.MoveMutableSegmentForward();
        tree.StartMergeOperation().Join();
        tree.OnBottomSegmentsMergeOperationEnded += (_, status) => result = status;
        tree.StartBottomSegmentsMergeOperation(0, 1).Join();
        Assert.That(tree.BottomSegments[0].Length, Is.EqualTo(4));
      }
      else
      {
        tree.Upsert(100, 2100);
        tree.MoveMutableSegmentForward();
        tree.OnMergeOperationEnded += (_, status) => result = status;
        tree.StartMergeOperation().Join();
        Assert.That(tree.DiskSegment.Length, Is.EqualTo(4));
      }
      Assert.That(result, Is.EqualTo(MergeResult.SUCCESS));
      Assert.That(calls, Is.Zero);
      foreach (var key in new[] { 0, 1, 2 })
        Assert.That(tree.TryGet(key, out _), Is.False,
            "Dropping the expired part must not resurrect the older live value.");
    }
    finally
    {
      tree.Dispose();
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  [Test]
  public void FullBottomMergeDropsOnlyOldestExpiredPartsAndKeepsNewerValues()
  {
    var values = new TrackingValueSerializer();
    var options = CreateOptions(values);
    var ranges = new List<int>();
    options.IsRangeDeleted = (in int firstKey, in int _, in int _, in int lastValue) =>
    {
      ranges.Add(firstKey);
      return lastValue < 1008;
    };
    var ids = new IncrementalIdProvider();
    var newer = CreateSegment(options, ids, key => key + 2000, [0, 1], [20, 21]);
    var oldest = CreateSegment(options, ids, [0, 1, 2, 3], [4], [5, 6, 7], [10, 11, 12]);
    using var tree = CreateTree(options, ids, new NullDiskSegment<int, int>(), [newer, oldest]);
    try
    {
      values.Reads.Clear();
      MergeResult? result = null;
      tree.OnBottomSegmentsMergeOperationEnded += (_, status) => result = status;
      tree.StartBottomSegmentsMergeOperation(0, int.MaxValue).Join();

      Assert.That(result, Is.EqualTo(MergeResult.SUCCESS));
      Assert.That(tree.BottomSegments[0].Length, Is.EqualTo(7));
      Assert.That(ranges, Is.EqualTo(new[] { 0, 4, 5, 10 }));
      Assert.That(values.Reads.Where(x => x >= 1002 && x < 1008), Is.Empty);
      foreach (var key in new[] { 0, 1, 10, 11, 12, 20, 21 })
        Assert.That(tree.TryGet(key, out _), Is.True);
      Assert.That(tree.TryGet(0, out var value0), Is.True);
      Assert.That(value0, Is.EqualTo(2000));
    }
    finally
    {
      tree.Dispose();
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  [Test]
  public void FalseRangePredicatePreservesLiveInteriorBetweenExpiredEndpoints()
  {
    var options = CreateOptions(new TrackingValueSerializer());
    var calls = 0;
    options.IsRangeDeleted = (in int _, in int _, in int _, in int _) =>
    {
      ++calls;
      return false;
    };
    var ids = new IncrementalIdProvider();
    var disk = CreateSegment(options, ids, key => key == 1 ? 2001 : 1000, [0, 1, 2]);
    using var tree = CreateTree(options, ids, disk);
    try
    {
      tree.Upsert(100, -1);
      tree.MoveMutableSegmentForward();
      tree.StartMergeOperation().Join();
      Assert.That(calls, Is.EqualTo(1));
      Assert.That(tree.DiskSegment.Length, Is.EqualTo(1));
      Assert.That(tree.TryGet(1, out var value), Is.True);
      Assert.That(value, Is.EqualTo(2001));
    }
    finally
    {
      tree.Dispose();
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  [TestCase(false)]
  [TestCase(true)]
  public void RangePredicateReceivesComparerOrderedEndpoints(bool descending)
  {
    var options = CreateOptions(new TrackingValueSerializer());
    if (descending)
      options.Comparer = new Int32ComparerDescending();
    var ranges = new List<(int, int)>();
    options.IsRangeDeleted = (in int firstKey, in int firstValue, in int lastKey, in int lastValue) =>
    {
      ranges.Add((firstKey, lastKey));
      return firstValue < 1008 && lastValue < 1008;
    };
    var ids = new IncrementalIdProvider();
    var disk = descending ?
        CreateSegment(options, ids, [12, 11, 10], [3, 2, 1, 0]) :
        CreateSegment(options, ids, [0, 1, 2, 3], [10, 11, 12]);
    using var tree = CreateTree(options, ids, disk);
    try
    {
      tree.Upsert(100, 2100);
      tree.MoveMutableSegmentForward();
      tree.StartMergeOperation().Join();
      Assert.That(tree.DiskSegment.Length, Is.EqualTo(4));
      Assert.That(ranges, Is.EqualTo(descending ?
          new[] { (12, 10), (3, 0) } : new[] { (0, 3), (10, 12) }));
    }
    finally
    {
      tree.Dispose();
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  [Test]
  public void SingleFileInputUsesRecordDeletionWithoutCallingRangeDelegate()
  {
    var options = CreateOptions(new TrackingValueSerializer());
    var calls = 0;
    options.IsRangeDeleted = (in int _, in int _, in int _, in int _) =>
    {
      ++calls;
      return true;
    };
    var ids = new IncrementalIdProvider();
    using var creator = new DiskSegmentCreator<int, int>(options, ids);
    creator.Append(0, 1000, IteratorPosition.None);
    creator.Append(10, 1010, IteratorPosition.None);
    using var tree = CreateTree(options, ids, creator.CreateReadOnlyDiskSegment());
    try
    {
      tree.Upsert(100, 2100);
      tree.MoveMutableSegmentForward();
      tree.StartMergeOperation().Join();
      Assert.That(calls, Is.Zero);
      Assert.That(tree.DiskSegment.Length, Is.EqualTo(2));
      Assert.That(tree.TryGet(10, out var value), Is.True);
      Assert.That(value, Is.EqualTo(1010));
    }
    finally
    {
      tree.Dispose();
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  [Test]
  public void FactoryAndClonedOptionsPreserveOptionalRangeDelegate()
  {
    var options = CreateOptions(new TrackingValueSerializer());
    IsRangeDeletedDelegate<int, int> predicate = (in int _, in int _, in int _, in int _) => false;
    using var tree = new ZoneTreeFactory<int, int>()
        .Configure(x =>
        {
          x.RandomAccessDeviceManager = options.RandomAccessDeviceManager;
          x.WriteAheadLogProvider = options.WriteAheadLogProvider;
        })
        .SetIsRangeDeletedDelegate(predicate)
        .OpenOrCreate();
    try
    {
      Assert.That(tree.Maintenance.CloneOptions().IsRangeDeleted, Is.SameAs(predicate));
    }
    finally
    {
      tree.Dispose();
      options.RandomAccessDeviceManager.DropStore();
    }
  }

  static ZoneTreeOptions<int, int> CreateOptions(TrackingValueSerializer values)
  {
    var logger = new ConsoleLogger(LogLevel.Error);
    return new ZoneTreeOptions<int, int>
    {
      Comparer = new Int32ComparerAscending(),
      KeyHasher = new DefaultKeyHasher<int>(),
      KeySerializer = new Int32Serializer(),
      ValueSerializer = values,
      IsDeleted = (in int _, in int value) => value < 1008,
      MarkValueDeleted = (ref int value) => value = -1,
      Logger = logger,
      WriteAheadLogProvider = new NullWriteAheadLogProvider(),
      RandomAccessDeviceManager = new RandomAccessDeviceManager(
          logger, new InMemoryFileStreamProvider(), "data/RangeDeletionMergeTests"),
      DiskSegmentOptions = new DiskSegmentOptions
      {
        CompressionMethod = CompressionMethod.None,
        CompressionLevel = 0,
        CompressionBlockSize = 1024,
        MinimumRecordCount = 1,
        MaximumRecordCount = 100,
        DefaultSparseArrayStepSize = 0,
      },
      AllowUnsafeOptionValues = true,
    };
  }

  static IDiskSegment<int, int> CreateSegment(
      ZoneTreeOptions<int, int> options, IIncrementalIdProvider ids, params int[][] parts)
      => CreateSegment(options, ids, key => key + 1000, parts);

  static IDiskSegment<int, int> CreateSegment(
      ZoneTreeOptions<int, int> options, IIncrementalIdProvider ids,
      Func<int, int> valueForKey, params int[][] parts)
  {
    using var creator = new MultiPartDiskSegmentCreator<int, int>(options, ids);
    foreach (var keys in parts)
    {
      using var partCreator = new DiskSegmentCreator<int, int>(options, ids);
      foreach (var key in keys)
        partCreator.Append(key, valueForKey(key), IteratorPosition.None);
      creator.Append(partCreator.CreateReadOnlyDiskSegment(),
          keys[0], keys[^1], valueForKey(keys[0]), valueForKey(keys[^1]));
    }
    return creator.CreateReadOnlyDiskSegment();
  }

  static ZoneTree<int, int> CreateTree(
      ZoneTreeOptions<int, int> options, IncrementalIdProvider ids,
      IDiskSegment<int, int> disk, IDiskSegment<int, int>[] bottom = null)
  {
    var mutable = new MutableSegment<int, int>(options, ids.NextId(), new IncrementalIdProvider());
    return new ZoneTree<int, int>(options, new ZoneTreeMeta(), [], mutable, disk, bottom ?? [], ids.LastId);
  }

  static bool DataExists(ZoneTreeOptions<int, int> options, IDiskSegment<int, int> part) =>
      options.RandomAccessDeviceManager.DeviceExists(
          part.SegmentId, DiskSegmentConstants.DataCategory, isCompressed: true);

  sealed class TrackingValueSerializer : ISerializer<int>
  {
    readonly Int32Serializer Serializer = new();

    public readonly List<int> Reads = new();

    public Memory<byte> Serialize(in int entry) => Serializer.Serialize(entry);

    public int Deserialize(Memory<byte> bytes)
    {
      var value = Serializer.Deserialize(bytes);
      Reads.Add(value);
      return value;
    }
  }
}
