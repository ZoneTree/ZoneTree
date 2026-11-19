using ZoneTree.AbstractFileStream;
using ZoneTree.Collections;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>
/// Tests how iterators collect segments without the tree locks: a dropped tree refuses new iterators instead of
/// retrying forever, and the read-only queue's newest-first walk never skips an item that stayed queued during it.
/// </summary>
public sealed class SegmentCollectionTests
{
  [Test]
  public void CreatingAnIteratorOnADroppedTreeThrowsInsteadOfRetrying()
  {
    var tree = new ZoneTreeFactory<int, int>(new InMemoryFileStreamProvider())
        .SetDataDirectory("data/IteratorOnDroppedTree")
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.None)
        .OpenOrCreate();
    for (var key = 0; key < 100; ++key)
      tree.Upsert(key, key + 1);
    tree.Maintenance.MoveMutableSegmentForward();
    tree.Maintenance.StartMergeOperation()?.Join();
    tree.Maintenance.Drop();

    // The retired disk segment is still the published one, so a retry would read it again forever.
    var attempt = Task.Run(() =>
    {
      using var iterator = tree.CreateIterator(IteratorType.NoRefresh);
      iterator.Next();
    });
    var finished = Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(10))).Result == attempt;
    Assert.That(finished, Is.True, "creating the iterator did not finish");
    Assert.That(attempt.Exception?.InnerException, Is.InstanceOf<InvalidOperationException>());
  }

  [Test]
  public async Task TheNewestFirstWalkKeepsEveryItemThatStayedQueued()
  {
    // A small queue that the producer and the consumer keep cycling, so its ring wraps around many times while
    // readers walk it. The queue never fills, so it keeps its first chunk.
    var queue = new SingleProducerSingleConsumerQueue<Item>();
    long enqueued = 0;
    long dequeued = 0;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var producer = Task.Run(() =>
    {
      while (!stop.IsCancellationRequested)
      {
        if (Volatile.Read(ref enqueued) - Volatile.Read(ref dequeued) >= 8)
          continue;
        queue.Enqueue(new Item(enqueued));
        Volatile.Write(ref enqueued, enqueued + 1);
      }
    });
    var consumer = Task.Run(() =>
    {
      while (!stop.IsCancellationRequested)
      {
        if (Volatile.Read(ref dequeued) == Volatile.Read(ref enqueued))
          continue;
        // Counted before the item leaves, so a reader never expects an item that is being dequeued.
        Volatile.Write(ref dequeued, dequeued + 1);
        queue.TryDequeue(out _);
      }
    });
    var failures = 0L;
    var walks = 0L;
    var readers = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount - 2)).Select(_ => Task.Run(() =>
    {
      while (!stop.IsCancellationRequested)
      {
        // Items enqueued before the walk and still queued after it were queued during the whole walk.
        var enqueuedBefore = Volatile.Read(ref enqueued);
        var items = queue.ToLastInFirstArray();
        var dequeuedAfter = Volatile.Read(ref dequeued);
        var expected = Math.Max(0, enqueuedBefore - dequeuedAfter);
        var present = items.Count(item => item.Sequence >= dequeuedAfter && item.Sequence < enqueuedBefore);
        var newestFirst = items.Zip(items.Skip(1)).All(pair => pair.First.Sequence > pair.Second.Sequence);
        if (present != expected || !newestFirst)
          Interlocked.Increment(ref failures);
        Interlocked.Increment(ref walks);
      }
    })).ToArray();
    await Task.WhenAll(readers.Append(producer).Append(consumer));

    Assert.Multiple(() =>
    {
      Assert.That(failures, Is.Zero, $"{failures} of {walks} walks lost an item or broke the order");
      Assert.That(walks, Is.GreaterThan(1000));
    });
  }

  sealed record Item(long Sequence);
}
