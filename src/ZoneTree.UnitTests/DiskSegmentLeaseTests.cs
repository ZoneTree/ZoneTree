using ZoneTree.Collections;
using ZoneTree.Comparers;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Segments.Block;
using ZoneTree.Segments.Disk;
using ZoneTree.Serializers;

namespace ZoneTree.UnitTests;

public sealed class DiskSegmentLeaseTests
{
  [Test]
  public void AllowsRepeatedAttachAndDetachWhileActive()
  {
    var segment = CreateSegment();

    segment.AttachIterator();
    segment.DetachIterator();
    segment.AttachIterator();
    segment.DetachIterator();

    Assert.That(segment.DeleteCount, Is.Zero);
  }

  [Test]
  public void DefersDropUntilTheLastIteratorDetaches()
  {
    var segment = CreateSegment();
    segment.AttachIterator();
    segment.AttachIterator();

    segment.Drop();

    Assert.Multiple(() =>
    {
      Assert.That(segment.DeleteCount, Is.Zero);
      Assert.Throws<InvalidOperationException>(segment.AttachIterator);
    });

    segment.DetachIterator();
    Assert.That(segment.DeleteCount, Is.Zero);

    segment.DetachIterator();
    Assert.That(segment.DeleteCount, Is.EqualTo(1));
  }

  [Test]
  public void ConcurrentDetachesCompleteDropExactlyOnce()
  {
    const int iteratorCount = 64;
    var segment = CreateSegment();
    for (var i = 0; i < iteratorCount; ++i)
      segment.AttachIterator();
    segment.Drop();

    Parallel.For(0, iteratorCount, _ => segment.DetachIterator());

    Assert.That(segment.DeleteCount, Is.EqualTo(1));
  }

  [Test]
  public void ImmediateDropIsIdempotent()
  {
    var segment = CreateSegment();

    segment.Drop();
    segment.Drop();

    Assert.That(segment.DeleteCount, Is.EqualTo(1));
  }

  [Test]
  public void ActiveReadDelaysDeviceDeletion()
  {
    var segment = CreateSegment();
    var readStripe = segment.BeginTestRead();
    Task drop = null;

    try
    {
      Assert.That(segment.ActiveReads, Is.EqualTo(1));
      drop = Task.Run(segment.Drop);

      Assert.That(
          SpinWait.SpinUntil(
              () => segment.IsDropInProgress,
              TimeSpan.FromSeconds(5)),
          Is.True);
      Assert.That(segment.DeleteStarted.IsSet, Is.False);
    }
    finally
    {
      segment.EndTestRead(readStripe);
      drop?.Wait(TimeSpan.FromSeconds(5));
    }

    Assert.Multiple(() =>
    {
      Assert.That(segment.ActiveReads, Is.Zero);
      Assert.That(segment.DeleteCount, Is.EqualTo(1));
    });
  }

  [Test]
  public void ConcurrentDropWaitsForInProgressDeletion()
  {
    var segment = CreateSegment();
    segment.BlockDelete = true;

    var firstDrop = Task.Run(segment.Drop);
    Task secondDrop = null;
    try
    {
      Assert.That(segment.DeleteStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);

      using var secondDropStarted = new ManualResetEventSlim();
      secondDrop = Task.Run(() =>
      {
        secondDropStarted.Set();
        segment.Drop();
      });
      Assert.That(secondDropStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);
      Assert.That(secondDrop.Wait(TimeSpan.FromMilliseconds(100)), Is.False);
    }
    finally
    {
      segment.AllowDelete.Set();
      firstDrop.Wait(TimeSpan.FromSeconds(5));
      secondDrop?.Wait(TimeSpan.FromSeconds(5));
    }

    Assert.That(segment.DeleteCount, Is.EqualTo(1));
  }

  [Test]
  public void FailedImmediateDropCanBeRetried()
  {
    var segment = CreateSegment();
    segment.ThrowOnDelete = true;

    Assert.Throws<InvalidOperationException>(segment.Drop);
    Assert.That(segment.DeleteCount, Is.EqualTo(1));

    segment.ThrowOnDelete = false;
    segment.Drop();

    Assert.That(segment.DeleteCount, Is.EqualTo(2));
  }

  [Test]
  public void FailedDeferredDropIsReportedAndCanBeRetried()
  {
    var segment = CreateSegment();
    Exception reportedException = null;
    segment.DropFailureReporter = (_, exception) => reportedException = exception;
    segment.ThrowOnDelete = true;
    segment.AttachIterator();
    segment.Drop();

    Assert.DoesNotThrow(segment.DetachIterator);
    Assert.Multiple(() =>
    {
      Assert.That(segment.DeleteCount, Is.EqualTo(1));
      Assert.That(reportedException, Is.TypeOf<InvalidOperationException>());
    });

    segment.ThrowOnDelete = false;
    segment.Drop();

    Assert.That(segment.DeleteCount, Is.EqualTo(2));
  }

  [Test]
  public void RejectsUnbalancedDetach()
  {
    var segment = CreateSegment();

    Assert.Throws<InvalidOperationException>(segment.DetachIterator);
  }

  [Test]
  public void FailedIteratorCreationReleasesTheDiskSegmentLease()
  {
    using var tree = new ZoneTreeFactory<int, int>(new AbstractFileStream.InMemoryFileStreamProvider())
        .SetDataDirectory("failed-iterator-lease")
        .ConfigureWriteAheadLogOptions(x => x.WriteAheadLogMode = WriteAheadLogMode.None)
        .ConfigureDiskSegmentOptions(x => x.DiskSegmentMode = DiskSegmentMode.SingleDiskSegment)
        .OpenOrCreate();
    for (var i = 0; i < 100; ++i)
      tree.Upsert(i, i);
    tree.Maintenance.MoveMutableSegmentForward();
    tree.Maintenance.StartMergeOperation().Join();
    var segment = (DiskSegment<int, int>)tree.Maintenance.DiskSegment;

    // The prefetch buffer cannot be allocated, so creating the disk segment
    // iterator fails after the segment is attached.
    using (var iterator = tree.CreateIterator(new IteratorOptions
    {
      DiskSegmentPrefetchSize = int.MaxValue
    }))
    {
      Assert.Throws<OutOfMemoryException>(() => iterator.SeekFirst());
    }

    Assert.That(segment.IteratorReaderCount, Is.Zero);
  }

  static TestDiskSegment CreateSegment()
  {
    var options = new ZoneTreeOptions<int, int>
    {
      Comparer = new Int32ComparerAscending(),
      KeySerializer = new Int32Serializer(),
      ValueSerializer = new Int32Serializer()
    };
    return new TestDiskSegment(options);
  }

  sealed class TestDiskSegment(
      ZoneTreeOptions<int, int> options) : DiskSegment<int, int>(1, options)
  {
    int deleteCount;

    public int DeleteCount => Volatile.Read(ref deleteCount);

    public int ActiveReads => ActiveReadCount;

    public bool IsDropInProgress => IsDropping;

    public bool ThrowOnDelete;

    public bool BlockDelete;

    public readonly ManualResetEventSlim DeleteStarted = new();

    public readonly ManualResetEventSlim AllowDelete = new();

    public override int ReadBufferCount => 0;

    public int BeginTestRead() => BeginRead();

    public void EndTestRead(int stripeIndex) => EndRead(stripeIndex);

    protected override int ReadKey(long index, BlockPin blockPin) => (int)index;

    protected override int ReadValue(long index, BlockPin blockPin) => (int)index;

    public override int ReadEntries(
        long startIndex,
        int count,
        int[] keys,
        int[] values,
        int destinationIndex,
        BlockPin blockPin)
    {
      return 0;
    }

    protected override void DeleteDevices()
    {
      Interlocked.Increment(ref deleteCount);
      DeleteStarted.Set();
      if (BlockDelete)
        AllowDelete.Wait();
      if (ThrowOnDelete)
        throw new InvalidOperationException("Test drop failure.");
    }

    public override int ReleaseReadBuffers(long ticks) => 0;

    public override void SetDefaultSparseArray(
        IReadOnlyList<SparseArrayEntry<int, int>> defaultSparseArray)
    {
      SparseArray = defaultSparseArray;
    }
  }
}
