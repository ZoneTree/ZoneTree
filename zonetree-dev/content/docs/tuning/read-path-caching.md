# Read-Path Caching

ZoneTree uses several read-side accelerators. They operate at different layers
and should not be treated as one global cache:

1. mutable-segment Bloom filters reject definitely absent keys,
2. sparse indexes narrow disk searches,
3. decompressed blocks avoid repeated I/O and decompression,
4. materialized-entry chunks avoid repeated key/value deserialization,
5. circular caches retain individual disk keys and values,
6. search hints accelerate sequential `TryGet` streams,
7. iterators can prefetch consecutive disk entries.

## Mutable-Segment Bloom Filter

Every mutable segment can maintain a fixed-size concurrent Bloom filter. A
negative result skips that segment's B+Tree lookup and synchronization; a
possible match continues through the normal lookup path. The default is `8`
requested bits per item, and `0` disables the filter.

See [mutable-segment Bloom filters](../concepts/bloom-filters.md) for sizing, false-positive
behavior, hasher correctness, lifetime, and tuning guidance.

## Sparse Disk Index

`DiskSegmentOptions.DefaultSparseArrayStepSize` controls how frequently a disk
segment records a key/value position in its sparse index. The default is
`1024`; `0` disables the default sparse array.

Smaller steps consume more memory and narrow the local disk search. Larger
steps retain fewer sparse entries but leave more keys to search near the target.

## Decompressed Block Cache

Compressed disk segments are read in compression blocks. When a read touches a
block, ZoneTree loads and decompresses the block into memory. Reusing that
`DecompressedBlock` avoids repeating storage I/O and decompression.

`DiskSegmentOptions.CompressionBlockSize` defaults to `4 MB`. Larger blocks can
improve compression and sequential throughput but increase random-read
amplification and retained memory.

The maintainer releases inactive decompressed blocks:

```csharp
using var maintainer = zoneTree.CreateMaintainer();

maintainer.BlockCacheLifeTime = TimeSpan.FromMinutes(2);
maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromSeconds(30);
```

The defaults are a one-minute inactive lifetime and a 30-second cleanup
interval. Released managed memory may remain reserved by .NET for later reuse.

## Materialized Entry Cache

Reading a decompressed block still requires turning stored bytes into `TKey`
and `TValue`. Each block can retain that result in aligned chunks of 16 entries.

`DiskSegmentOptions.MaterializedEntryCacheSize` is the maximum number of chunk
slots per decompressed block. The default is `4096`, allowing at most 65,536
materialized entries per block when the block contains that many entries. `0`
disables the cache.

Chunks are aligned by absolute disk index rather than by caller request. An
iterator prefetching 16 entries and another prefetching 100 can therefore reuse
the same chunks instead of storing overlapping request-shaped batches. Partial
chunks are valid; only materialized positions are returned as cache hits.

The cache is bounded and collision-based. When more chunk indexes map to the
available slots, later publications can replace older chunks. Publications are
immutable, so concurrent readers either observe a complete matching chunk or
miss and read the block normally.

Single-record materializations are not inserted. This avoids polluting the
cache while sparse indexes are built and favors batched reads that can reuse
both key and value materialization.

## Circular Key And Value Caches

Each disk segment also has bounded caches for individual deserialized keys and
values:

* `KeyCacheSize` and `ValueCacheSize`, default `1024`,
* `KeyCacheRecordLifeTimeInMillisecond` and
  `ValueCacheRecordLifeTimeInMillisecond`, default `10_000`.

These caches are useful for repeated access to the same disk record indexes.
They are separate from the decompressed-block and materialized-entry caches.
Cache sizes may be arbitrary non-negative values; they do not need to be powers
of two. A size of `0` disables the corresponding cache.

## Sequential TryGet Search Hints

Disk segments track successful `TryGet` positions per calling thread. After
consecutive ascending or descending record positions are detected, ZoneTree
prefetches adjacent key/value entries and attempts to satisfy the next lookup
from that buffer.

`DiskSegmentOptions.SearchHintPrefetchSize` defaults to `16`; `0` disables the
feature. Buffers are allocated lazily per disk segment and calling thread.

Search hints are intended for lookup streams that resolve to adjacent record
positions. If the next requested key does not match the hinted adjacent entry,
ZoneTree invalidates the prefetched hint and falls back to the normal
sparse-index and binary-search path. A very large prefetch size can waste reads
and retain substantial thread-local memory, especially when many threads touch
many disk segments.

## Iterator Prefetch

`IteratorOptions.DiskSegmentPrefetchSize` controls batched disk entry reads for
an iterator. The default is `0`, and values below `2` disable prefetching.

```csharp
using var iterator = zoneTree.CreateIterator(new IteratorOptions
{
    DiskSegmentPrefetchSize = 16,
    ContributeToTheBlockCache = false
});
```

Prefetching can reduce per-entry overhead in range scans. It also allocates key
and value buffers per low-level disk iterator and can read beyond an early scan
termination point. Benchmark the full query shape, not only iterator movement.

## Iterator Block-Cache Contribution

Disk-segment iterator reads do not contribute to the block cache by default.
This avoids retaining the decompressed blocks touched by a scan after the
iterator moves on.

Set `ContributeToTheBlockCache` to `true` to add those decompressed blocks to
the shared block cache:

```csharp
using var iterator = zoneTree.CreateIterator(new IteratorOptions
{
    ContributeToTheBlockCache = true
});
```

Contributing retains the scanned blocks in memory until inactive block-cache
cleanup releases them.

## Tuning Order

Measure before changing several layers at once:

1. keep maintenance healthy so obsolete segments do not multiply read work,
2. verify key layout and access locality,
3. inspect Bloom-filter effectiveness for absent mutable-segment lookups,
4. tune sparse-index density and compression-block size,
5. budget decompressed and materialized block memory,
6. tune iterator prefetch or search hints for measured sequential access,
7. enlarge circular caches only for a demonstrated repeated-record working set.

Track latency, throughput, allocation, retained memory, storage reads, and
single-thread versus parallel behavior together.
