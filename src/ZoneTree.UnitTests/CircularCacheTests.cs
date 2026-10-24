using ZoneTree.Segments.Disk;

namespace ZoneTree.UnitTests;

public sealed class CircularCacheTests
{
  [Test]
  public void DistributesIndexesWithIdenticalLowBits()
  {
    var cache = new CircularCache<int>(8, 10_000);
    var first = 1;
    var second = 2;

    cache.TryAdd(0, ref first);
    cache.TryAdd(8, ref second);

    Assert.That(cache.TryGet(0, out var firstResult), Is.True);
    Assert.That(firstResult, Is.EqualTo(first));
    Assert.That(cache.TryGet(8, out var secondResult), Is.True);
    Assert.That(secondResult, Is.EqualTo(second));
  }

  [Test]
  public void EmptySlotsDoNotMatchIndexZero()
  {
    var cache = new CircularCache<int>(8, 10_000);
    Assert.That(cache.TryGet(0, out _), Is.False);
    var value = 5;
    cache.TryAdd(1, ref value);
    for (var i = 0; i < 64; ++i)
      Assert.That(cache.TryGet(i, out _), Is.EqualTo(i == 1), $"index {i}");
    Assert.That(cache.ReleaseInactiveCacheRecords(), Is.EqualTo(0));
    Assert.That(cache.TryGet(0, out _), Is.False);
  }

  [Test]
  public void ZeroSizedCacheKeepsNothing()
  {
    var cache = new CircularCache<int>(0, 10_000);
    var value = 5;
    Assert.That(cache.TryAdd(0, ref value), Is.False);
    Assert.That(cache.TryGet(0, out _), Is.False);
    Assert.That(cache.ReleaseInactiveCacheRecords(), Is.EqualTo(0));
  }

  [Test]
  public void ReleasesExpiredRecordsAndClears()
  {
    var cache = new CircularCache<string>(1024, 10_000);
    for (var i = 0; i < 4; ++i)
    {
      var value = "v" + i;
      cache.TryAdd(i, ref value);
    }
    Assert.That(cache.ReleaseInactiveCacheRecords(), Is.EqualTo(0));
    for (var i = 0; i < 4; ++i)
    {
      Assert.That(cache.TryGet(i, out var kept), Is.True);
      Assert.That(kept, Is.EqualTo("v" + i));
    }

    cache.RecordLifeTimeInMillisecond = -60_000;
    Assert.That(cache.ReleaseInactiveCacheRecords(), Is.EqualTo(4));
    for (var i = 0; i < 4; ++i)
      Assert.That(cache.TryGet(i, out _), Is.False);
    Assert.That(cache.ReleaseInactiveCacheRecords(), Is.EqualTo(0));

    var again = "again";
    cache.TryAdd(2, ref again);
    cache.Clear();
    Assert.That(cache.TryGet(2, out _), Is.False);
    cache.TryAdd(2, ref again);
    Assert.That(cache.TryGet(2, out var added), Is.True);
    Assert.That(added, Is.EqualTo("again"));
  }

  readonly record struct CheckedRecord(long Index, long Salt, long Check);

  [Test]
  public void ConcurrentReadersNeverSeeTornOrMismatchedRecords()
  {
    // Records larger than a machine word can be torn by a racing writer.
    // Every record carries its index and a checksum of its fields.
    var cache = new CircularCache<CheckedRecord>(64, 0);
    var stop = false;
    var hits = 0L;
    var failures = 0L;
    void Write(int seed)
    {
      var random = new Random(seed);
      while (!Volatile.Read(ref stop))
      {
        var index = random.Next(512);
        var salt = random.NextInt64();
        var record = new CheckedRecord(index, salt, index ^ salt);
        cache.TryAdd(index, ref record);
        if ((salt & 255) == 0)
          cache.ReleaseInactiveCacheRecords();
      }
    }
    void Read(int seed)
    {
      var random = new Random(seed);
      while (!Volatile.Read(ref stop))
      {
        var index = random.Next(512);
        if (!cache.TryGet(index, out var record))
          continue;
        Interlocked.Increment(ref hits);
        if (record.Index != index || record.Check != (record.Index ^ record.Salt))
          Interlocked.Increment(ref failures);
      }
    }
    var tasks = Enumerable.Range(0, 4)
        .Select(i => Task.Factory.StartNew(() => Write(i), TaskCreationOptions.LongRunning))
        .Concat(Enumerable.Range(0, 4)
            .Select(i => Task.Factory.StartNew(() => Read(100 + i), TaskCreationOptions.LongRunning)))
        .ToArray();
    Thread.Sleep(1000);
    Volatile.Write(ref stop, true);
    Task.WaitAll(tasks);
    Assert.That(hits, Is.GreaterThan(0));
    Assert.That(failures, Is.EqualTo(0));
  }
}
