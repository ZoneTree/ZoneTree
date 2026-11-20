using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Exceptions;
using ZoneTree.Hashers;
using ZoneTree.Options;
using ZoneTree.Threading;

namespace ZoneTree.UnitTests;

public sealed class KeyLockTests
{
  static IZoneTree<int, int> CreateTree(int lockStripeCount = 1024) =>
      new ZoneTreeFactory<int, int>(new InMemoryFileStreamProvider())
          .SetDataDirectory("data")
          .SetLogger(SilentTestLogger.Instance)
          .Configure(options => options.LockStripeCount = lockStripeCount)
          .ConfigureWriteAheadLogOptions(options => options.Mode = WriteAheadLogMode.None)
          .OpenOrCreate();

  [Test]
  public void LockedIncrementsDoNotLoseUpdates([Values(1, 1024)] int lockStripeCount)
  {
    using var tree = CreateTree(lockStripeCount);
    const int threads = 8;
    const int increments = 2000;
    Parallel.For(0, threads, thread =>
    {
      for (var i = 0; i < increments; ++i)
      {
        var key = i % 4;
        using (tree.Lock(key))
        {
          tree.TryGet(key, out var count);
          tree.Upsert(key, count + 1);
        }
      }
    });
    for (var key = 0; key < 4; ++key)
    {
      Assert.That(tree.TryGet(key, out var count), Is.True);
      Assert.That(count, Is.EqualTo(threads * increments / 4));
    }
  }

  [Test]
  public async Task AsyncLocksCanBeHeldAcrossAwait()
  {
    using var tree = CreateTree();
    const int tasks = 8;
    const int increments = 200;
    await Task.WhenAll(Enumerable.Range(0, tasks).Select(_ => Task.Run(async () =>
    {
      for (var i = 0; i < increments; ++i)
      {
        using (await tree.LockAsync(7))
        {
          tree.TryGet(7, out var count);
          await Task.Yield();
          tree.Upsert(7, count + 1);
        }
      }
    })));
    Assert.That(tree.TryGet(7, out var total), Is.True);
    Assert.That(total, Is.EqualTo(tasks * increments));
  }

  [Test]
  public async Task SyncAndAsyncCallersExcludeEachOther()
  {
    using var tree = CreateTree();
    // A sync holder blocks an async caller on another thread.
    var scope = tree.Lock(3);
    var waiting = Task.Run(() => tree.LockAsync(3).AsTask());
    Assert.That(waiting.Wait(50), Is.False);
    scope.Dispose();
    Assert.That(waiting.Wait(TimeSpan.FromSeconds(10)), Is.True);
    waiting.Result.Dispose();
    // An async holder blocks a sync caller.
    using (await tree.LockAsync(3))
      Assert.That(Task.Run(() => tree.TryLock(3, TimeSpan.FromMilliseconds(50), out _)).Result, Is.False);
  }

  [Test]
  public void LockingKeysInOppositeOrdersDoesNotDeadlock([Values(1, 1024)] int lockStripeCount)
  {
    using var tree = CreateTree(lockStripeCount);
    void Transfer(int from, int to)
    {
      for (var i = 0; i < 2000; ++i)
      {
        using (tree.LockMany(from, to))
        {
          tree.TryGet(from, out var source);
          tree.TryGet(to, out var target);
          tree.Upsert(from, source - 1);
          tree.Upsert(to, target + 1);
        }
      }
    }
    var forward = Task.Run(() => Transfer(1, 2));
    var backward = Task.Run(() => Transfer(2, 1));
    Assert.That(Task.WaitAll([forward, backward], TimeSpan.FromSeconds(30)), Is.True);
    tree.TryGet(1, out var first);
    tree.TryGet(2, out var second);
    Assert.That(first + second, Is.Zero);
  }

  [Test]
  public void KeysThatShareALockAreLockedOnce()
  {
    // With one stripe every key shares the lock; a key repeated in the list shares it too.
    using var tree = CreateTree(1);
    using (tree.LockMany(1, 2, 2, 3))
      Assert.That(tree.TryLock(4, TimeSpan.Zero, out _), Is.False);
    Assert.That(tree.TryLock(4, TimeSpan.Zero, out var scope), Is.True);
    scope.Dispose();
  }

  [Test]
  public void TryLockFailsWhileTheKeyIsLocked()
  {
    using var tree = CreateTree();
    using (tree.Lock(5))
    {
      var waiting = Task.Run(() => tree.TryLock(5, TimeSpan.FromMilliseconds(20), out _));
      Assert.That(waiting.Result, Is.False);
      // A zero timeout does not wait, so the holder only learns that the lock is taken.
      Assert.That(tree.TryLock(5, TimeSpan.Zero, out var failed), Is.False);
      failed.Dispose();
    }
    Assert.That(tree.TryLock(5, TimeSpan.Zero, out var scope), Is.True);
    scope.Dispose();
  }

  [Test]
  public void WaitingForALockTheThreadHoldsThrows()
  {
    // With one stripe, a different key has the same lock too.
    using var tree = CreateTree(1);
    using (tree.Lock(1))
    {
      Assert.Throws<LockRecursionException>(() => tree.Lock(1));
      Assert.Throws<LockRecursionException>(() => tree.Lock(2));
      Assert.Throws<LockRecursionException>(() => tree.TryLock(2, TimeSpan.FromSeconds(1), out _));
    }
    using var striped = CreateTree();
    using (striped.Lock(3))
      Assert.Throws<LockRecursionException>(() => striped.LockMany(1, 2, 3, 4, 5));
    // LockMany released the locks it took before it reached the held one.
    AssertFree(striped, [1, 2, 3, 4, 5]);
  }

  [Test]
  public void ARecursionOnAKeyExplainsThatKeysShareStripes()
  {
    // With one stripe, a different key has the same lock too.
    using var tree = CreateTree(1);
    using (tree.Lock(1))
    {
      var sync = Assert.Throws<LockRecursionException>(() => tree.Lock(2));
      Assert.That(sync.Message, Does.Contain("Keys can share a stripe").And.Contain("one LockMany call"));
      Assert.That(sync.InnerException, Is.TypeOf<LockRecursionException>());
      Assert.That(() => tree.TryLock(2, TimeSpan.FromSeconds(1), out _),
          Throws.TypeOf<LockRecursionException>().With.Message.Contains("Keys can share a stripe"));
      Assert.That(() => tree.LockMany(2, 3),
          Throws.TypeOf<LockRecursionException>().With.Message.Contains("Keys can share a stripe"));
      Assert.That(() => tree.LockAsync(2).AsTask().GetAwaiter().GetResult(),
          Throws.TypeOf<LockRecursionException>().With.Message.Contains("one LockManyAsync call"));
      Assert.That(() => tree.LockManyAsync([2, 3]).AsTask().GetAwaiter().GetResult(),
          Throws.TypeOf<LockRecursionException>().With.Message.Contains("one LockManyAsync call"));
    }
    AssertFree(tree, [1, 2, 3]);
  }

  [Test]
  public void WaitingAsynchronouslyForALockTheThreadHoldsThrows()
  {
    // With one stripe, a different key has the same lock too.
    using var tree = CreateTree(1);
    using (tree.Lock(1))
    {
      var single = tree.LockAsync(2);
      Assert.That(single.IsFaulted, Is.True);
      Assert.That(() => single.AsTask().GetAwaiter().GetResult(), Throws.TypeOf<LockRecursionException>());
      Assert.That(() => tree.LockManyAsync([2, 3]).AsTask().GetAwaiter().GetResult(),
          Throws.TypeOf<LockRecursionException>());
    }
    AssertFree(tree, [1, 2, 3]);
    using var striped = CreateTree();
    using (striped.Lock(3))
      Assert.That(() => striped.LockManyAsync([1, 2, 3, 4, 5]).AsTask().GetAwaiter().GetResult(),
          Throws.TypeOf<LockRecursionException>());
    // LockManyAsync released the locks it took before it reached the held one.
    AssertFree(striped, [1, 2, 3, 4, 5]);
  }

  [Test]
  public void ReleasingAFreeLockThrows()
  {
    using var tree = CreateTree();
    var scope = tree.Lock(1);
    scope.Dispose();
    Assert.Throws<SynchronizationLockException>(scope.Dispose);
  }

  [Test]
  public void TryLockAcceptsAnInfiniteTimeoutAndRefusesNegativeOnes()
  {
    using var tree = CreateTree();
    Assert.That(tree.TryLock(1, Timeout.InfiniteTimeSpan, out var scope), Is.True);
    scope.Dispose();
    Assert.Throws<ArgumentOutOfRangeException>(() => tree.TryLock(1, TimeSpan.FromMilliseconds(-2), out _));
    // A negative fraction is not infinite, nor zero.
    Assert.Throws<ArgumentOutOfRangeException>(() => tree.TryLock(1, TimeSpan.FromMilliseconds(-1.5), out _));
    Assert.Throws<ArgumentOutOfRangeException>(() => tree.TryLock(1, TimeSpan.FromMilliseconds(-0.5), out _));
    Assert.Throws<ArgumentOutOfRangeException>(() => tree.TryLock(1, TimeSpan.MaxValue, out _));
  }

  [TestCase(0, 0)]
  [TestCase(0.0001, 1)]
  [TestCase(0.5, 1)]
  [TestCase(1, 1)]
  [TestCase(1.5, 2)]
  [TestCase(int.MaxValue, int.MaxValue)]
  public void TimeoutsRoundUpToWholeMilliseconds(double timeout, long milliseconds)
  {
    Assert.That(HybridLock.ToMilliseconds(TimeSpan.FromMilliseconds(timeout)), Is.EqualTo(milliseconds));
  }

  [Test]
  public void OnlyTheInfiniteTimeoutIsInfinite()
  {
    Assert.That(HybridLock.ToMilliseconds(Timeout.InfiniteTimeSpan), Is.EqualTo(Timeout.Infinite));
    Assert.Throws<ArgumentOutOfRangeException>(() => HybridLock.ToMilliseconds(TimeSpan.FromMilliseconds(-1.5)));
    Assert.Throws<ArgumentOutOfRangeException>(() => HybridLock.ToMilliseconds(TimeSpan.FromMilliseconds(-0.5)));
    Assert.Throws<ArgumentOutOfRangeException>(() => HybridLock.ToMilliseconds(TimeSpan.FromTicks(-1)));
    // Just over the largest timeout, which rounds up past it.
    Assert.Throws<ArgumentOutOfRangeException>(
        () => HybridLock.ToMilliseconds(TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromTicks(1)));
  }

  [Test]
  public void AnAsyncWaiterIsNotStarvedBySyncCallers()
  {
    using var tree = CreateTree();
    using var stop = new CancellationTokenSource();
    var hammers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
    {
      while (!stop.IsCancellationRequested)
      {
        using (tree.Lock(1))
          Thread.SpinWait(200);
      }
    }, TaskCreationOptions.LongRunning)).ToArray();
    try
    {
      // A waiter that keeps losing the freed lock to running callers gets it handed over.
      var waiting = tree.LockAsync(1).AsTask();
      Assert.That(waiting.Wait(TimeSpan.FromSeconds(10)), Is.True);
      waiting.Result.Dispose();
    }
    finally
    {
      stop.Cancel();
      Task.WaitAll(hammers);
    }
  }

  [Test]
  public async Task ContendedAsyncWaitsReuseTheirWaiters()
  {
    using var tree = CreateTree();
    for (var i = 0; i < 1000; ++i)
    {
      // An async holder has no thread, so this thread can still wait for the key asynchronously.
      var holder = await tree.LockAsync(1);
      var waiting = tree.LockAsync(1);
      Assert.That(waiting.IsCompleted, Is.False);
      holder.Dispose();
      (await waiting).Dispose();
    }
    AssertFree(tree, [1]);
  }

  [Test]
  public async Task AZeroTimeoutTakesAFreedLockThatAWaiterIsOnItsWayTo()
  {
    using var tree = CreateTree();
    var taken = 0;
    for (var i = 0; i < 100; ++i)
    {
      var holder = await tree.LockAsync(1);
      var waiting = tree.LockAsync(1);
      // The release wakes the async waiter, which needs a thread pool thread to take the lock.
      holder.Dispose();
      if (tree.TryLock(1, TimeSpan.Zero, out var scope))
      {
        ++taken;
        scope.Dispose();
      }
      (await waiting).Dispose();
    }
    Assert.That(taken, Is.GreaterThan(0));
  }

  [Test]
  public async Task AWaiterThatLostTheFreedLockCanStillBeCancelled()
  {
    using var tree = CreateTree();
    for (var i = 0; i < 100; ++i)
    {
      var holder = await tree.LockAsync(1);
      using var cancellation = new CancellationTokenSource();
      var waiting = tree.LockAsync(1, cancellation.Token).AsTask();
      holder.Dispose();
      // Takes the freed lock before the woken waiter runs, so the waiter queues again, and
      // the cancellation comes at a different point of that each time.
      if (!tree.TryLock(1, TimeSpan.Zero, out holder))
      {
        (await waiting).Dispose();
        continue;
      }
      Thread.SpinWait(i * 20);
      cancellation.Cancel();
      // The waiter gives up while the lock is still held. A blocking wait keeps the lock
      // from TryLock on its thread.
      var completed = Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult();
      Assert.That(completed, Is.SameAs(waiting));
      Assert.That(waiting.IsCanceled, Is.True);
      holder.Dispose();
    }
    AssertFree(tree, [1]);
  }

  [Test]
  public async Task MixedCallersNeverHoldALockTogether()
  {
    // One stripe puts every key on one lock, so all callers contend.
    using var tree = CreateTree(1);
    var inside = 0;
    var overlaps = 0;
    var acquired = 0;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    void Hold()
    {
      if (Interlocked.Increment(ref inside) != 1)
        Interlocked.Increment(ref overlaps);
      Thread.SpinWait(50);
      Interlocked.Decrement(ref inside);
      Interlocked.Increment(ref acquired);
    }
    var sync = Enumerable.Range(0, 4).Select(worker => Task.Factory.StartNew(() =>
    {
      var random = new Random(worker);
      while (!stop.IsCancellationRequested)
      {
        switch (random.Next(3))
        {
          case 0:
            using (tree.Lock(random.Next(8)))
              Hold();
            break;
          case 1:
            if (tree.TryLock(random.Next(8), TimeSpan.Zero, out var free))
              using (free)
                Hold();
            break;
          default:
            if (tree.TryLock(random.Next(8), TimeSpan.FromMilliseconds(random.Next(3)), out var timed))
              using (timed)
                Hold();
            break;
        }
      }
    }, TaskCreationOptions.LongRunning));
    var async = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
    {
      var random = new Random(100 + worker);
      while (!stop.IsCancellationRequested)
      {
        using var cancellation = new CancellationTokenSource();
        if (random.Next(2) == 0)
          cancellation.CancelAfter(random.Next(2));
        try
        {
          using (await tree.LockAsync(random.Next(8), cancellation.Token))
          {
            Hold();
            await Task.Yield();
          }
        }
        catch (OperationCanceledException)
        {
        }
      }
    }));
    await Task.WhenAll(sync.Concat(async));
    Assert.That(overlaps, Is.Zero);
    Assert.That(acquired, Is.GreaterThan(0));
    AssertFree(tree, Enumerable.Range(0, 8));
  }

  static void AssertFree(IZoneTree<int, int> tree, IEnumerable<int> keys)
  {
    foreach (var key in keys)
    {
      Assert.That(tree.TryLock(key, TimeSpan.Zero, out var scope), Is.True, $"key {key}");
      scope.Dispose();
    }
  }

  static void AssertLocked(IZoneTree<int, int> tree, IEnumerable<int> keys)
  {
    foreach (var key in keys)
      Assert.That(tree.TryLock(key, TimeSpan.Zero, out _), Is.False, $"key {key}");
  }

  [Test]
  public async Task SeveralKeysAreLockedAndReleasedTogether()
  {
    // Five keys take more than two stripes, so the scope holds them in an array.
    int[] keys = [1, 2, 3, 4, 5];
    using var tree = CreateTree();
    using (tree.LockMany(keys))
      AssertLocked(tree, keys);
    AssertFree(tree, keys);
    using (await tree.LockManyAsync(keys))
      AssertLocked(tree, keys);
    AssertFree(tree, keys);
  }

  [Test]
  public async Task LockManyAsyncTakesTheKeysOfAnySequence()
  {
    using var tree = CreateTree();
    int[] keys = [1, 2, 3];
    // An array, a list, a collection expression and a sequence that is not a list.
    IEnumerable<int>[] sequences = [keys, new List<int>(keys), [1, 2, 3], keys.Where(_ => true)];
    foreach (var sequence in sequences)
    {
      using (await tree.LockManyAsync(sequence))
        AssertLocked(tree, keys);
      AssertFree(tree, keys);
    }
    // The span and array overloads.
    using (await tree.LockManyAsync([1, 2, 3]))
      AssertLocked(tree, keys);
    AssertFree(tree, keys);
    using (await tree.LockManyAsync(keys))
      AssertLocked(tree, keys);
    AssertFree(tree, keys);
    using (await LockStackKeys(tree))
      AssertLocked(tree, keys);
    AssertFree(tree, keys);

    static ValueTask<HybridLockScope> LockStackKeys(IZoneTree<int, int> tree)
    {
      Span<int> stackKeys = stackalloc int[] { 3, 1, 2 };
      return tree.LockManyAsync(stackKeys);
    }
  }

  [Test]
  public async Task LockManyAsyncOfACollectionExpressionAllocatesNothingWhileTheKeysAreFree()
  {
    using var tree = CreateTree();
    (await tree.LockManyAsync([1, 2])).Dispose();
    var completed = 0;
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 100; ++i)
    {
      var pending = tree.LockManyAsync([1, 2]);
      if (pending.IsCompletedSuccessfully)
        ++completed;
      pending.Result.Dispose();
    }
    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
    Assert.That(completed, Is.EqualTo(100));
    Assert.That(allocated, Is.Zero);
  }

  [Test]
  public async Task LockManyAsyncWaitsForAHeldKeyAfterTakingTheFreeOnes()
  {
    using var tree = CreateTree();
    int[] keys = [1, 2, 3, 4, 5];
    foreach (var held in keys)
    {
      var holder = await tree.LockAsync(held);
      var waiting = tree.LockManyAsync(keys).AsTask();
      Assert.That(waiting.Wait(20), Is.False);
      holder.Dispose();
      using (await waiting)
        AssertLocked(tree, keys);
      AssertFree(tree, keys);
    }
  }

  [TestCase(new[] { 8, 9 })]
  [TestCase(new[] { 1, 2, 3, 4, 5 })]
  public async Task CancelledLockAsyncHoldsNothing(int[] keys)
  {
    using var tree = CreateTree();
    // Holding each key in turn blocks the call at each position of the stripe order,
    // so it cancels after locking none, some, or all but one of the other stripes.
    foreach (var held in keys)
    {
      using var cancellation = new CancellationTokenSource();
      using (await tree.LockAsync(held))
      {
        var waiting = tree.LockManyAsync(keys, cancellation.Token).AsTask();
        Assert.That(waiting.Wait(50), Is.False);
        cancellation.Cancel();
        Assert.That(() => waiting.GetAwaiter().GetResult(), Throws.InstanceOf<OperationCanceledException>());
        AssertFree(tree, keys.Where(key => key != held));
      }
      AssertFree(tree, keys);
    }
  }

  [Test]
  public void ACancelledCallLocksNothingEvenWhenTheKeysAreFree()
  {
    using var tree = CreateTree();
    int[] keys = [1, 2, 3, 4, 5];
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var single = tree.LockAsync(1, cancellation.Token);
    Assert.That(single.IsCanceled, Is.True);
    // The span, array and sequence overloads.
    ValueTask<HybridLockScope>[] many =
    [
      tree.LockManyAsync([1, 2], cancellation.Token),
      tree.LockManyAsync(keys, cancellation.Token),
      tree.LockManyAsync(keys.Where(_ => true), cancellation.Token)
    ];
    foreach (var pending in many)
      Assert.That(pending.IsCanceled, Is.True);
    Assert.That(() => single.AsTask().GetAwaiter().GetResult(), Throws.InstanceOf<OperationCanceledException>());
    AssertFree(tree, keys);
  }

  sealed class OrdinalIgnoreCaseKeyHasher : IKeyHasher<string>
  {
    public int GetHashCode(in string key) => StringComparer.OrdinalIgnoreCase.GetHashCode(key);
  }

  [Test]
  public void KeysThatTheComparerConsidersEqualShareALock()
  {
    using var tree = new ZoneTreeFactory<string, int>(new InMemoryFileStreamProvider())
        .SetDataDirectory("data")
        .SetLogger(SilentTestLogger.Instance)
        .SetComparer(new StringOrdinalIgnoreCaseComparerAscending())
        .SetKeyHasher(new OrdinalIgnoreCaseKeyHasher())
        .ConfigureWriteAheadLogOptions(options => options.Mode = WriteAheadLogMode.None)
        .OpenOrCreate();
    using (tree.Lock("key"))
      Assert.That(tree.TryLock("KEY", TimeSpan.Zero, out _), Is.False);
  }

  [Test]
  public void AHasherThatDoesNotMatchTheComparerIsRefusedForLocks()
  {
    // The default string hasher tells "a" from "A", which the comparer considers equal.
    ZoneTreeFactory<string, int> Factory(int lockStripeCount) =>
        new ZoneTreeFactory<string, int>(new InMemoryFileStreamProvider())
            .SetDataDirectory("data")
            .SetLogger(SilentTestLogger.Instance)
            .SetComparer(new StringOrdinalIgnoreCaseComparerAscending())
            .SetMutableSegmentBloomFilterBitsPerItem(0)
            .Configure(options => options.LockStripeCount = lockStripeCount)
            .ConfigureWriteAheadLogOptions(options => options.Mode = WriteAheadLogMode.None);
    var exception = Assert.Throws<InvalidOptionValueException>(() => Factory(1024).Validate());
    Assert.That(exception.Message, Does.Contain("Configure a KeyHasher that matches the Comparer"));
    // One lock does not use the hasher.
    Factory(1).Validate();
  }

  [Test]
  public void TransactionalTreesUseOneLockStripe()
  {
    // A transactional tree has no key locks, so a hasher that does not match the comparer is fine.
    var factory = new ZoneTreeFactory<string, int>(new InMemoryFileStreamProvider())
        .SetDataDirectory("data")
        .SetLogger(SilentTestLogger.Instance)
        .SetComparer(new StringOrdinalIgnoreCaseComparerAscending())
        .SetMutableSegmentBloomFilterBitsPerItem(0)
        .Configure(options => options.LockStripeCount = 16);
    using (factory.OpenOrCreateTransactional()) { }
    Assert.That(factory.Options.LockStripeCount, Is.EqualTo(1));
  }

  [Test]
  public void NullKeysCanBeLocked()
  {
    using var strings = new ZoneTreeFactory<string, int>(new InMemoryFileStreamProvider())
        .SetDataDirectory("data")
        .SetLogger(SilentTestLogger.Instance)
        .ConfigureWriteAheadLogOptions(options => options.Mode = WriteAheadLogMode.None)
        .OpenOrCreate();
    using (strings.Lock(null))
      Assert.That(strings.TryLock(null, TimeSpan.Zero, out _), Is.False);

    // The default serializers consider a null byte array empty, and so does its key hasher.
    using var bytes = new ZoneTreeFactory<byte[], int>(new InMemoryFileStreamProvider())
        .SetDataDirectory("data")
        .SetLogger(SilentTestLogger.Instance)
        .ConfigureWriteAheadLogOptions(options => options.Mode = WriteAheadLogMode.None)
        .OpenOrCreate();
    using (bytes.Lock((byte[])null))
      Assert.That(bytes.TryLock([], TimeSpan.Zero, out _), Is.False);
  }

  [Test]
  public void ANullListOfKeysIsRefused()
  {
    using var tree = CreateTree();
    Assert.Throws<ArgumentNullException>(() => tree.LockMany((int[])null));
    Assert.ThrowsAsync<ArgumentNullException>(async () => (await tree.LockManyAsync(null)).Dispose());
  }

  [TestCase(0)]
  [TestCase(KeyedLock<int>.MaximumStripeCount + 1)]
  public void KeyLocksRefuseAStripeCountOutOfRange(int stripeCount)
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new KeyedLock<int>(new DefaultKeyHasher<int>(), stripeCount));
  }

  [Test]
  public void KeyedLockRefusesAMissingKeyHasherForMoreThanOneStripe()
  {
    var exception = Assert.Throws<ArgumentNullException>(() => _ = new KeyedLock<int>(null, 1024));
    Assert.That(exception.ParamName, Is.EqualTo("keyHasher"));
    Assert.That(exception.Message, Does.Contain("stripe count of 1"));
    // One lock needs no hasher.
    new KeyedLock<int>(null, 1).Lock(1).Dispose();
  }

  sealed class FailingKeyHasher : IKeyHasher<int>
  {
    public int GetHashCode(in int key) => throw new InvalidOperationException("The hasher failed.");
  }

  [Test]
  public void AsyncCallsReportAHasherErrorThroughTheirTask()
  {
    var locks = new KeyedLock<int>(new FailingKeyHasher(), 1024);
    Assert.Throws<InvalidOperationException>(() => locks.Lock(1));
    ValueTask<HybridLockScope>[] pending =
    [
      locks.LockAsync(1),
      locks.LockManyAsync([1, 2]),
      locks.LockManyAsync(new[] { 1, 2 }),
      locks.LockManyAsync(new List<int> { 1, 2 }.Where(_ => true))
    ];
    foreach (var call in pending)
    {
      Assert.That(call.IsFaulted, Is.True);
      Assert.That(() => call.AsTask().GetAwaiter().GetResult(), Throws.TypeOf<InvalidOperationException>());
    }
  }

  [Test]
  public async Task KeyedLockLocksKeysOnItsOwn()
  {
    var locks = new KeyedLock<int>(new DefaultKeyHasher<int>(), 1024);
    int[] keys = [1, 2, 3];
    // A zero timeout does not wait, so the thread that holds a key only learns that it is taken.
    void AssertHeld(params int[] held)
    {
      foreach (var key in held)
        Assert.That(locks.TryLock(key, TimeSpan.Zero, out _), Is.False, $"key {key}");
    }
    void AssertFreed(params int[] freed)
    {
      foreach (var key in freed)
      {
        Assert.That(locks.TryLock(key, TimeSpan.Zero, out var scope), Is.True, $"key {key}");
        scope.Dispose();
      }
    }
    using (locks.Lock(1))
      AssertHeld(1);
    using (locks.LockMany(1, 2, 3))
      AssertHeld(keys);
    using (locks.LockMany(keys))
      AssertHeld(keys);
    using (await locks.LockAsync(1))
      AssertHeld(1);
    using (await locks.LockManyAsync([1, 2, 3]))
      AssertHeld(keys);
    using (await locks.LockManyAsync(keys))
      AssertHeld(keys);
    using (await locks.LockManyAsync(new List<int>(keys)))
      AssertHeld(keys);
    AssertFreed(keys);
    Assert.Throws<ArgumentNullException>(() => locks.LockMany((int[])null));
    Assert.ThrowsAsync<ArgumentNullException>(async () => (await locks.LockManyAsync((int[])null)).Dispose());
    Assert.ThrowsAsync<ArgumentNullException>(
        async () => (await locks.LockManyAsync((IEnumerable<int>)null)).Dispose());
  }

  [TestCase(0)]
  [TestCase((1 << 20) + 1)]
  public void LockStripeCountOutOfRangeIsRefusedEvenWithUnsafeValues(int lockStripeCount)
  {
    var factory = new ZoneTreeFactory<int, int>()
        .Configure(options =>
        {
          options.AllowUnsafeOptionValues = true;
          options.LockStripeCount = lockStripeCount;
        });
    Assert.Throws<InvalidOptionValueException>(() => factory.Validate());
  }
}
