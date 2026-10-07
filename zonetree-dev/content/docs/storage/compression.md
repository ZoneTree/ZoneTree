# Compression

ZoneTree supports compression in two different storage paths:

* WAL compression for recent log records,
* disk segment compression for persistent random-access sorted data.

Both use the same compression method/level model, but they serve different workloads and have different default block sizes.

## Compression Methods

ZoneTree supports:

* `LZ4`
* `Zstd`
* `Brotli`
* `Gzip`
* `None`

Compression levels are validated against the selected method. The default profile uses Zstd compression because it is a good general-purpose balance for storage-engine workloads.

## WAL Compression

The default WAL mode is `AsyncCompressed`.

Default WAL compression:

```text
method: Zstd
level:  Zstd0
block:  256 KB
```

Compressed WALs reduce log size and can improve write-path IO when data compresses well. They also add compression work. The async compressed mode is the default because it keeps WAL protection enabled while preserving high write throughput for most applications.

Sync compressed WAL uses the same compressed-block WAL file shape and tail behavior. Use Sync WAL when the application specifically needs the plain synchronous WAL path; use sync-compressed WAL when compressed WAL storage or throughput matters and its tail durability tradeoff is acceptable.

## Disk Segment Compression

Disk segment compression is block-based random-access compression.

Default disk compression:

```text
method: Zstd
level:  Zstd0
block:  4 MB
```

Compressed disk segment files store sorted data in compressed blocks. ZoneTree can seek to a block, read it, decompress it, and then serve keys or values from the decompressed block.

This is different from compressing a whole file as one stream. ZoneTree keeps random access.

## Block Cache Interaction

Compressed disk reads use a decompressed block cache. When a block is read, ZoneTree can keep the decompressed block in memory so nearby or repeated reads do not have to read and decompress the same block again.

The important relationship is:

```text
disk compression block size -> decompressed block cache unit size
```

Larger disk compression blocks can improve compression ratio and sequential read behavior, but each cached block keeps more decompressed data in memory. Smaller blocks can make random reads cheaper, but may reduce compression density and increase metadata overhead.

Block cache cleanup is controlled by the maintainer:

* `BlockCacheLifeTime`
* `InactiveBlockCacheCleanupInterval`

See [read path caching](../tuning/read-path-caching.md).

## Why WAL And Disk Defaults Differ

ZoneTree intentionally uses smaller blocks for WAL and larger blocks for disk segments.

WAL blocks serve recent writes and recovery. Smaller blocks keep log recovery and tail handling more responsive.

Disk segment blocks serve persistent sorted data. Larger blocks can improve compression density and are often a better fit for range reads, sparse lookups, and block cache reuse.

## When Compression Helps

Compression can help when:

* values are text, JSON, repeated structures, or otherwise compressible,
* storage bandwidth is more expensive than CPU,
* disk space matters,
* read workloads repeatedly touch nearby compressed blocks,
* large disk segments benefit from smaller physical files.

## When Compression Costs More

Compression may cost more than it saves when:

* payloads are already compressed,
* CPU is the bottleneck,
* values are tiny and mostly random,
* random reads constantly touch cold blocks,
* compression block size is too large for the read pattern.

For large values, compression should be tested with real payloads. A large JSON value and a large already-compressed image have very different storage behavior.

## Configuration

Configure WAL compression through `WriteAheadLogOptions`:

```csharp
using ZoneTree.Options;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .ConfigureWriteAheadLogOptions(options =>
    {
        options.CompressionMethod = CompressionMethod.Zstd;
        options.CompressionLevel = CompressionLevels.Zstd0;
        options.CompressionBlockSize = 256 * 1024;
    })
    .OpenOrCreate();
```

Configure disk segment compression through `DiskSegmentOptions`:

```csharp
using ZoneTree.Options;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .ConfigureDiskSegmentOptions(options =>
    {
        options.CompressionMethod = CompressionMethod.Zstd;
        options.CompressionLevel = CompressionLevels.Zstd0;
        options.CompressionBlockSize = 4 * 1024 * 1024;
    })
    .OpenOrCreate();
```

`SetDiskSegmentCompressionBlockSize` is also available as a convenience method on `ZoneTreeFactory`.

## Practical Model

Think about compression in ZoneTree this way:

* WAL compression optimizes recent durable log records.
* Disk compression optimizes persistent sorted segment files.
* Disk compression block size also defines the main read-cache unit.
* The maintainer controls inactive decompressed block cleanup.
* Real payloads matter more than generic compression claims.
