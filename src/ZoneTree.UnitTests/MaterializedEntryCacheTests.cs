using ZoneTree.Options;
using ZoneTree.Segments.Block;

namespace ZoneTree.UnitTests;

public sealed class MaterializedEntryCacheTests
{
  [TestCase(0, 2)]
  [TestCase(7, 8)]
  [TestCase(14, 2)]
  [TestCase(15, 2)]
  [TestCase(16, 15)]
  [TestCase(15, 17)]
  [TestCase(31, 32)]
  public void RangesUseSourceAndDestinationOffsets(
      long startIndex,
      int count)
  {
    const int sourceIndex = 3;
    const int destinationIndex = 5;
    var cache = MaterializedEntryCache<long, string>.GetOrCreate(
        CreateBlock(),
        128);
    var sourceKeys = new long[sourceIndex + count + 2];
    var sourceValues = new string[sourceIndex + count + 2];
    for (var i = 0; i < count; ++i)
    {
      sourceKeys[sourceIndex + i] = startIndex + i;
      sourceValues[sourceIndex + i] = $"value-{startIndex + i}";
    }
    cache.Add(
        startIndex,
        count,
        sourceKeys,
        sourceValues,
        sourceIndex);

    var destinationKeys = Enumerable.Repeat(-1L, destinationIndex + count + 2).ToArray();
    var destinationValues = Enumerable.Repeat("untouched", destinationIndex + count + 2).ToArray();
    var copied = cache.TryCopy(
        startIndex,
        count,
        destinationKeys,
        destinationValues,
        destinationIndex);

    Assert.That(copied, Is.True);
    Assert.That(
        destinationKeys.Skip(destinationIndex).Take(count),
        Is.EqualTo(Enumerable.Range(0, count).Select(i => startIndex + i)));
    Assert.That(
        destinationValues.Skip(destinationIndex).Take(count),
        Is.EqualTo(Enumerable.Range(0, count).Select(i => $"value-{startIndex + i}")));
    Assert.That(destinationKeys.Take(destinationIndex), Is.All.EqualTo(-1L));
    Assert.That(destinationValues.Take(destinationIndex), Is.All.EqualTo("untouched"));
  }

  [Test]
  public void DefaultSizeIs4096Chunks()
  {
    Assert.That(DiskSegmentDefaultValues.MaterializedEntryCacheSize, Is.EqualTo(4096));
    Assert.That(new DiskSegmentOptions().MaterializedEntryCacheSize, Is.EqualTo(4096));
  }

  [Test]
  public void ZeroSizeDisablesCacheCreation()
  {
    var block = CreateBlock();

    var cache = MaterializedEntryCache<int, int>.GetOrCreate(block, 0);

    Assert.That(cache, Is.Null);
    Assert.That(block.MaterializedEntries, Is.Null);
  }

  [Test]
  public void NullBlockAndNegativeSizeDisableCacheCreation()
  {
    Assert.That(
        MaterializedEntryCache<int, int>.GetOrCreate(null, 16),
        Is.Null);

    var block = CreateBlock();
    Assert.That(
        MaterializedEntryCache<int, int>.GetOrCreate(block, -1),
        Is.Null);
    Assert.That(block.MaterializedEntries, Is.Null);
  }

  [Test]
  public void RepeatedCreationReturnsSameTypedCache()
  {
    var block = CreateBlock();

    var first = MaterializedEntryCache<int, int>.GetOrCreate(block, 16);
    var second = MaterializedEntryCache<int, int>.GetOrCreate(block, 32);

    Assert.That(second, Is.SameAs(first));
  }

  [Test]
  public void BlockAcceptsOnlyOneMaterializedEntryType()
  {
    var block = CreateBlock();

    var first = MaterializedEntryCache<int, int>.GetOrCreate(block, 16);
    var conflicting = MaterializedEntryCache<string, long>.GetOrCreate(block, 16);

    Assert.That(first, Is.Not.Null);
    Assert.That(conflicting, Is.Null);
  }

  [Test]
  public void ConcurrentCreationPublishesOneInstance()
  {
    var block = CreateBlock();
    var caches = new MaterializedEntryCache<int, int>[256];

    Parallel.For(0, caches.Length, i =>
        caches[i] = MaterializedEntryCache<int, int>.GetOrCreate(block, 16));

    Assert.That(caches, Is.All.SameAs(caches[0]));
  }

  [Test]
  public void SingleRecordAndNonPositiveCountsAreNotCached()
  {
    var cache = MaterializedEntryCache<int, int>.GetOrCreate(CreateBlock(), 16);
    cache.Add(10, 1, new[] { 10 }, new[] { 110 }, 0);
    cache.Add(10, 0, Array.Empty<int>(), Array.Empty<int>(), 0);
    cache.Add(10, -1, Array.Empty<int>(), Array.Empty<int>(), 0);

    Assert.That(cache.TryCopy(10, 1, new int[1], new int[1], 0), Is.False);
    Assert.That(cache.TryCopy(10, 0, Array.Empty<int>(), Array.Empty<int>(), 0), Is.False);
    Assert.That(cache.TryCopy(10, -1, Array.Empty<int>(), Array.Empty<int>(), 0), Is.False);
  }

  [Test]
  public void UnalignedOverlappingRangesReuseCanonicalChunks()
  {
    var cache = MaterializedEntryCache<int, int>.GetOrCreate(CreateBlock(), 16);
    var firstKeys = Enumerable.Range(5, 16).ToArray();
    var firstValues = firstKeys.Select(x => x + 100).ToArray();
    cache.Add(5, 16, firstKeys, firstValues, 0);

    var secondKeys = Enumerable.Range(0, 16).ToArray();
    var secondValues = secondKeys.Select(x => x + 100).ToArray();
    cache.Add(0, 16, secondKeys, secondValues, 0);

    var keys = new int[16];
    var values = new int[16];
    Assert.That(cache.TryCopy(5, 16, keys, values, 0), Is.True);
    Assert.That(keys, Is.EqualTo(firstKeys));
    Assert.That(values, Is.EqualTo(firstValues));
  }

  [Test]
  public void LargerPrefetchCanServeSmallerUnalignedRead()
  {
    var cache = MaterializedEntryCache<int, int>.GetOrCreate(CreateBlock(), 16);
    var prefetchedKeys = Enumerable.Range(0, 64).ToArray();
    var prefetchedValues = prefetchedKeys.Select(x => x + 100).ToArray();
    cache.Add(0, 64, prefetchedKeys, prefetchedValues, 0);

    var keys = new int[16];
    var values = new int[16];
    Assert.That(cache.TryCopy(7, 16, keys, values, 0), Is.True);
    Assert.That(keys, Is.EqualTo(Enumerable.Range(7, 16)));
    Assert.That(values, Is.EqualTo(Enumerable.Range(107, 16)));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void PartialWritesCompleteCanonicalChunk(bool reverseOrder)
  {
    var cache = MaterializedEntryCache<int, int>.GetOrCreate(CreateBlock(), 16);
    var firstStartIndex = reverseOrder ? 8 : 0;
    var secondStartIndex = reverseOrder ? 0 : 8;
    cache.Add(
        firstStartIndex,
        8,
        Enumerable.Range(firstStartIndex, 8).ToArray(),
        Enumerable.Range(firstStartIndex + 100, 8).ToArray(),
        0);

    Assert.That(TryCopyFullChunk(cache, 0), Is.False);

    cache.Add(
        secondStartIndex,
        8,
        Enumerable.Range(secondStartIndex, 8).ToArray(),
        Enumerable.Range(secondStartIndex + 100, 8).ToArray(),
        0);

    Assert.That(TryCopyFullChunk(cache, 0), Is.True);
  }

  [Test]
  public void MissingMiddleChunkMakesMultiChunkReadMiss()
  {
    var cache = MaterializedEntryCache<int, int>.GetOrCreate(CreateBlock(), 64);
    AddFullChunk(cache, 0);
    AddFullChunk(cache, 2);

    Assert.That(TryCopyFullChunk(cache, 0), Is.True);
    Assert.That(TryCopyFullChunk(cache, 2), Is.True);
    Assert.That(cache.TryCopy(0, 48, new int[48], new int[48], 0), Is.False);
  }

  [Test]
  public void CacheOwnsReferenceTypeSourceArrays()
  {
    var cache = MaterializedEntryCache<string, string>.GetOrCreate(CreateBlock(), 16);
    var keys = Enumerable.Range(0, 16).Select(i => $"key-{i}").ToArray();
    var values = Enumerable.Range(0, 16).Select(i => $"value-{i}").ToArray();
    cache.Add(0, 16, keys, values, 0);

    Array.Fill(keys, "changed-key");
    Array.Fill(values, "changed-value");

    var cachedKeys = new string[16];
    var cachedValues = new string[16];
    Assert.That(cache.TryCopy(0, 16, cachedKeys, cachedValues, 0), Is.True);
    Assert.That(cachedKeys, Is.EqualTo(Enumerable.Range(0, 16).Select(i => $"key-{i}")));
    Assert.That(cachedValues, Is.EqualTo(Enumerable.Range(0, 16).Select(i => $"value-{i}")));
  }

  [Test]
  public void IndexesAboveIntMaxValueRemainDistinct()
  {
    var startIndex = (long)int.MaxValue + 37;
    var cache = MaterializedEntryCache<long, string>.GetOrCreate(CreateBlock(), 64);
    var keys = Enumerable.Range(0, 32).Select(i => startIndex + i).ToArray();
    var values = keys.Select(x => $"value-{x}").ToArray();
    cache.Add(startIndex, 32, keys, values, 0);

    var cachedKeys = new long[32];
    var cachedValues = new string[32];
    Assert.That(cache.TryCopy(startIndex, 32, cachedKeys, cachedValues, 0), Is.True);
    Assert.That(cachedKeys, Is.EqualTo(keys));
    Assert.That(cachedValues, Is.EqualTo(values));
  }

  [Test]
  public void OneSlotRetainsOnlyOneChunk()
  {
    var cache = MaterializedEntryCache<int, int>.GetOrCreate(CreateBlock(), 1);
    AddFullChunk(cache, 0);
    AddFullChunk(cache, 1);

    Assert.That(TryCopyFullChunk(cache, 0), Is.False);
    Assert.That(TryCopyFullChunk(cache, 1), Is.True);
  }

  [Test]
  public void ArbitrarySizeDistributesChunksAcrossAllSlots()
  {
    const int cacheSize = 3;
    var block = CreateBlock();
    var cache = MaterializedEntryCache<int, int>.GetOrCreate(block, cacheSize);
    var chunkIndexes = FindChunkIndexForEachSlot(cacheSize);

    foreach (var chunkIndex in chunkIndexes)
      AddFullChunk(cache, chunkIndex);

    foreach (var chunkIndex in chunkIndexes)
      Assert.That(TryCopyFullChunk(cache, chunkIndex), Is.True);
  }

  [Test]
  public void LargePrefetchCannotRetainMoreChunksThanSlots()
  {
    const int cacheSize = 4;
    const int startIndex = 3;
    const int count = 64 * 16 + 7;
    var cache = MaterializedEntryCache<int, int>.GetOrCreate(CreateBlock(), cacheSize);
    var keys = Enumerable.Range(startIndex, count).ToArray();
    var values = keys.Select(x => x + 100).ToArray();
    cache.Add(startIndex, keys.Length, keys, values, 0);

    var retainedChunks = Enumerable.Range(0, 65)
        .Count(chunkIndex => TryCopyFullChunk(cache, chunkIndex));

    Assert.That(retainedChunks, Is.LessThanOrEqualTo(cacheSize));
    Assert.That(retainedChunks, Is.GreaterThan(0));
  }

  [Test]
  public void ConcurrentReadsNeverReturnIncorrectPublishedValues()
  {
    const int chunkCount = 64;
    var cache = MaterializedEntryCache<int, int>.GetOrCreate(CreateBlock(), 32);
    var incorrectCopies = 0;

    Parallel.For(0, 20_000, iteration =>
    {
      var chunkIndex = iteration % chunkCount;
      var startIndex = chunkIndex * 16;
      var keys = Enumerable.Range(startIndex, 16).ToArray();
      var values = keys.Select(x => x + 100).ToArray();
      cache.Add(startIndex, 16, keys, values, 0);

      var cachedKeys = new int[16];
      var cachedValues = new int[16];
      if (cache.TryCopy(startIndex, 16, cachedKeys, cachedValues, 0) &&
          (!cachedKeys.SequenceEqual(keys) || !cachedValues.SequenceEqual(values)))
      {
        Interlocked.Increment(ref incorrectCopies);
      }
    });

    Assert.That(incorrectCopies, Is.Zero);
  }

  static DecompressedBlock CreateBlock()
  {
    return new DecompressedBlock(
        0,
        Memory<byte>.Empty,
        CompressionMethod.None,
        0);
  }

  static void AddFullChunk(
      MaterializedEntryCache<int, int> cache,
      long chunkIndex)
  {
    var startIndex = checked((int)(chunkIndex * 16));
    var keys = Enumerable.Range(startIndex, 16).ToArray();
    var values = keys.Select(x => x + 100).ToArray();
    cache.Add(startIndex, 16, keys, values, 0);
  }

  static bool TryCopyFullChunk(
      MaterializedEntryCache<int, int> cache,
      long chunkIndex)
  {
    var startIndex = checked((int)(chunkIndex * 16));
    var keys = new int[16];
    var values = new int[16];
    return cache.TryCopy(startIndex, 16, keys, values, 0) &&
           keys.SequenceEqual(Enumerable.Range(startIndex, 16)) &&
           values.SequenceEqual(Enumerable.Range(startIndex + 100, 16));
  }

  static long[] FindChunkIndexForEachSlot(int cacheSize)
  {
    var result = new long[cacheSize];
    var found = new bool[cacheSize];
    var remaining = cacheSize;
    for (long chunkIndex = 0; remaining != 0; ++chunkIndex)
    {
      var slot = (int)(chunkIndex % cacheSize);
      if (found[slot])
        continue;
      result[slot] = chunkIndex;
      found[slot] = true;
      --remaining;
    }
    return result;
  }
}
