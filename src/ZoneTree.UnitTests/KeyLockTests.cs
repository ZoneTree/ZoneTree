using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Exceptions;
using ZoneTree.Hashers;
using ZoneTree.Options;

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
  public void SyncAndAsyncCallersExcludeEachOther()
  {
    using var tree = CreateTree();
    var scope = tree.Lock(3);
    var waiting = tree.LockAsync(3).AsTask();
    Assert.That(waiting.Wait(50), Is.False);
    scope.Dispose();
    Assert.That(waiting.Wait(TimeSpan.FromSeconds(10)), Is.True);
    waiting.Result.Dispose();
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
      // The lock is not reentrant, so the holder cannot take it again either.
      Assert.That(tree.TryLock(5, TimeSpan.FromMilliseconds(20), out var failed), Is.False);
      failed.Dispose();
    }
    Assert.That(tree.TryLock(5, TimeSpan.Zero, out var scope), Is.True);
    scope.Dispose();
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

  [TestCase(new[] { 8, 9 })]
  [TestCase(new[] { 1, 2, 3, 4, 5 })]
  public void CancelledLockAsyncHoldsNothing(int[] keys)
  {
    using var tree = CreateTree();
    // Holding each key in turn blocks the call at each position of the stripe order,
    // so it cancels after locking none, some, or all but one of the other stripes.
    foreach (var held in keys)
    {
      using var cancellation = new CancellationTokenSource();
      using (tree.Lock(held))
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
  [TestCase(KeyLocks<int>.MaximumStripeCount + 1)]
  public void KeyLocksRefuseAStripeCountOutOfRange(int stripeCount)
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new KeyLocks<int>(new DefaultKeyHasher<int>(), stripeCount));
  }

  [Test]
  public void LockingWithoutAKeyHasherTellsWhatToConfigure()
  {
    var locks = new KeyLocks<int>(null, 1024);
    var exception = Assert.Throws<MissingOptionException>(() => locks.Lock(1));
    Assert.That(exception.Message, Does.Contain("LockStripeCount"));
    // The async call reports the error through its task, not at the call.
    var pending = locks.LockAsync(1, default);
    Assert.That(pending.IsFaulted, Is.True);
    Assert.That(() => pending.AsTask().GetAwaiter().GetResult(), Throws.TypeOf<MissingOptionException>());
    // One lock needs no hasher.
    new KeyLocks<int>(null, 1).Lock(1).Dispose();
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
