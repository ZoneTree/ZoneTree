# Disk Segment Tuning

Disk-segment options control persistent file shape, search cost, decompression,
and the memory retained for disk reads. Start from defaults and change one
dimension at a time against a representative workload.

## Segment Mode And Part Size

`MultiPartDiskSegment` is the default. It divides large persistent segments
into reusable range parts, limiting how much unchanged data a localized merge
must rewrite.

`MinimumRecordCount` and `MaximumRecordCount` default to `1_500_000` and
`3_000_000`. ZoneTree randomizes new part targets inside that range to avoid
rigid fully packed boundaries. Existing small parts may be merged rather than
reused to prevent fragmentation.

Larger parts reduce file count and transitions during scans. Smaller parts can
improve localized reuse but increase file count, metadata, backup work, and
iterator transitions.

`DiskSegmentMaxItemCount` is a higher-level threshold and defaults to
`20_000_000` records.

## Compression

Disk segments default to Zstd level `0` with `4 MB` compression blocks.

Larger blocks can improve compression ratio and sequential throughput. Smaller
blocks reduce the bytes read, decompressed, and retained for random access. The
validated block-size range is `1 MB` through `64 MB` unless unsafe option values
are explicitly allowed.

## Sparse Index

`DefaultSparseArrayStepSize` defaults to `1024` records.

| Value | Tradeoff |
| --- | --- |
| smaller positive value | more sparse entries and memory, narrower local search |
| larger value | fewer entries and less memory, wider local search |
| `0` | disable default sparse-array creation and loading |

The right density depends on key comparison, storage latency, block size, and
whether reads are random, clustered, or sequential.

## Materialized Entries

`MaterializedEntryCacheSize` defaults to 4096 aligned chunk slots per
decompressed block. Each chunk holds up to 16 deserialized key/value pairs.

Increase it only when repeated or overlapping batched reads benefit from
deserialization reuse and memory remains acceptable. Reduce it for large
reference-type values or a broad hot-block working set. Set it to `0` to
disable materialized-entry caching.

Because chunks are index-aligned, iterator prefetch sizes do not have to be
multiples of 16. Requests smaller than 16 can fill part of a chunk; later reads
can complete and reuse it.

## Sequential Point Reads

`SearchHintPrefetchSize` defaults to `16`. It accelerates consecutive ascending
or descending `TryGet` calls by reading adjacent entries after a sequential
pattern is detected.

Use a small value for short ranges or frequent direction changes. Larger values
can help sustained streams but retain larger buffers per calling thread and
disk segment. Set `0` to disable search hints.

This option is independent of `IteratorOptions.DiskSegmentPrefetchSize`, which
controls batching inside explicit iterators.

## Circular Record Caches

`KeyCacheSize` and `ValueCacheSize` default to `1024`; lifetimes default to ten
seconds. They target repeated reads of individual disk record positions.

The sizes can be any non-negative values. Set a size to `0` to disable that
cache. Increase them only when a stable repeated-record working set produces a
measured gain.

## Decompressed Block Lifetime

Block lifetime belongs to the maintainer rather than `DiskSegmentOptions`:

```csharp
using var maintainer = zoneTree.CreateMaintainer();

maintainer.BlockCacheLifeTime = TimeSpan.FromMinutes(2);
maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromSeconds(30);
```

A longer lifetime helps repeated nearby reads but retains blocks and their
materialized entries longer.

## Example Profile

```csharp
using ZoneTree.Options;

using var zoneTree = new ZoneTreeFactory<long, string>()
    .ConfigureDiskSegmentOptions(options =>
    {
        options.DiskSegmentMode = DiskSegmentMode.MultiPartDiskSegment;
        options.DefaultSparseArrayStepSize = 1024;
        options.MaterializedEntryCacheSize = 4096;
        options.SearchHintPrefetchSize = 16;
        options.KeyCacheSize = 1024;
        options.ValueCacheSize = 1024;
    })
    .OpenOrCreate();
```

See [read-path caching](read-path-caching.md),
[memory usage](../storage/memory-usage.md), and
[write amplification](write-amplification.md).
