using ZoneTree.AbstractFileStream;
using ZoneTree.Collections;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>
/// Tests how iterators collect segments without the tree locks: a dropped tree refuses new iterators instead of
/// retrying forever, and the segment queue loses no change and reads in order while it changes.
/// </summary>
public sealed class SegmentCollectionTests
{
  [Test]
  public void CreatingAnIteratorOnADroppedTreeThrowsInsteadOfRetrying()
  {
    var tree = new ZoneTreeFactory<int, int>(new InMemoryFileStreamProvider())
        .SetDataDirectory("data/IteratorOnDroppedTree")
        .ConfigureWriteAheadLogOptions(options => options.Mode = WriteAheadLogMode.None)
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
  public async Task TheCopyOnWriteQueueLosesNoChangeAndReadsInOrderDuringChanges()
  {
    // The producer and the consumer change the queue at the same time, as a segment move and a merge can, while
    // readers check that every array they read holds consecutive items from the newest to the oldest. The
    // consumer leaves a few items queued, so the reads have an order to check.
    const int count = 100_000;
    const int readerCount = 2;
    var queue = new CopyOnWriteQueue<Item>();
    // A lost change stops the consumer; the deadline ends the test with a failure instead of a hang.
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var readersRunning = 0;
    // Own threads, so no worker waits for the thread pool to grow.
    var producer = Task.Factory.StartNew(() =>
    {
      while (Volatile.Read(ref readersRunning) < readerCount && !stop.IsCancellationRequested)
        Thread.Yield();
      for (var sequence = 0; sequence < count && !stop.IsCancellationRequested; ++sequence)
      {
        while (queue.Length >= 8 && !stop.IsCancellationRequested)
          Thread.Yield();
        queue.Enqueue(new Item(sequence));
      }
    }, TaskCreationOptions.LongRunning);
    var dequeued = new List<long>(count);
    var consumer = Task.Factory.StartNew(() =>
    {
      while (dequeued.Count < count && !stop.IsCancellationRequested)
      {
        if ((queue.Length < 4 && !producer.IsCompleted) || !queue.TryDequeue(out var item))
        {
          Thread.Yield();
          continue;
        }
        dequeued.Add(item.Sequence);
      }
    }, TaskCreationOptions.LongRunning);
    var failures = 0L;
    var orderedReads = 0L;
    var readers = Enumerable.Range(0, readerCount).Select(_ => Task.Factory.StartNew(() =>
    {
      Interlocked.Increment(ref readersRunning);
      while (!consumer.IsCompleted)
      {
        var items = queue.Items;
        for (var i = 1; i < items.Length; ++i)
        {
          if (items[i - 1].Sequence != items[i].Sequence + 1)
            Interlocked.Increment(ref failures);
        }
        if (items.Length > 1)
          Interlocked.Increment(ref orderedReads);
        // Lets the producer and the consumer run on a machine with fewer cores than threads.
        Thread.Yield();
      }
    }, TaskCreationOptions.LongRunning)).ToArray();
    await Task.WhenAll(readers.Append(producer).Append(consumer));

    Assert.Multiple(() =>
    {
      Assert.That(stop.IsCancellationRequested, Is.False, "the test reached its deadline, so the queue lost a change");
      Assert.That(dequeued, Is.EqualTo(Enumerable.Range(0, count).Select(sequence => (long)sequence)));
      Assert.That(failures, Is.Zero, $"{failures} of {orderedReads} reads were not consecutive from the newest");
      Assert.That(orderedReads, Is.GreaterThan(0), "no read saw more than one item");
      Assert.That(queue.IsEmpty, Is.True);
    });
  }

  sealed record Item(long Sequence);
}
