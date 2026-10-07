# Large Values

Large values change the economics of a ZoneTree. Most defaults are expressed as record counts, but large values are governed by bytes, serialization cost, compression behavior, and merge IO.

This page is about values such as:

* long strings,
* JSON documents,
* large byte arrays,
* serialized object payloads,
* mutable object graphs.

## Start With The Value Model

Decide whether ZoneTree should store the whole value or a smaller reference to it.

Good fits for storing directly:

* compact records,
* small documents,
* values that are read and updated as one unit,
* values that compress well and remain operationally manageable.

Consider storing metadata or a content reference when values are very large:

* content address,
* file/object-store path,
* blob id,
* offset into an external payload store,
* small searchable metadata plus external content.

ZoneTree is a storage engine. It can store large values, but the best product shape may be a compact ordered index in ZoneTree and large payloads elsewhere.

## Mutable Segment Size

The first large-value control is `MutableSegmentMaxItemCount`.

The default is `1_000_000` records. That can be reasonable for compact records, but `1_000_000` large strings or documents can be far too much memory.

Tune this by expected byte size, not only by record count.

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/large-values")
    .SetMutableSegmentMaxItemCount(10_000)
    .OpenOrCreate();
```

Lower values move data toward read-only segments and disk sooner. That reduces active mutable memory, but can increase merge frequency. Keep a maintainer alive so read-only segments do not accumulate.

## Disk Part Size

Large values also change multipart disk segment tuning.

`MinimumRecordCount` and `MaximumRecordCount` are record counts. With large values, the same count can represent much more disk data and much more merge work.

For large values, use lower multipart part counts so each local rewrite unit stays reasonable.

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/large-values")
    .ConfigureDiskSegmentOptions(options =>
    {
        options.MinimumRecordCount = 50_000;
        options.MaximumRecordCount = 150_000;
    })
    .OpenOrCreate();
```

This gives multipart merge a smaller horizontal unit. Clean old parts can still be carried forward, but changed ranges do not have to rewrite as much payload data.

See [write amplification](write-amplification.md).

## Disk Segment Max Item Count

`DiskSegmentMaxItemCount` controls when `DiskSegment` is moved to bottom segments.

The default is `20_000_000` records. For large values, that may represent a very large disk segment.

Use a lower value when you want smaller operational boundaries:

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/large-values")
    .SetDiskSegmentMaxItemCount(2_000_000)
    .OpenOrCreate();
```

Tune this together with multipart part counts. `DiskSegmentMaxItemCount` controls the vertical boundary. `MinimumRecordCount` and `MaximumRecordCount` control the horizontal part size inside that disk segment.

## Compression

Large text values, JSON, and repetitive documents often compress well. Already-compressed payloads usually do not.

ZoneTree uses compression for both WAL and disk segment storage by default. Compression can reduce IO and storage size, but it adds CPU cost.

For disk segments, compression block size also affects read memory. A cached disk block is held decompressed. The default disk compression block size is `4 MB`.

For large random reads, smaller disk compression blocks may reduce read amplification and block cache memory pressure. For sequential reads and compressible data, larger blocks may be better.

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/large-values")
    .SetDiskSegmentCompressionBlockSize(1024 * 1024)
    .OpenOrCreate();
```

Benchmark with representative values.

See [compression](../storage/compression.md) and [read-path caching](read-path-caching.md).

## WAL Cost

Large values make WAL records larger. The default async compressed WAL is usually the right starting point because it keeps WAL protection enabled while compressing and writing in the background.

If WAL files are large or write latency is sensitive, test with real payloads and storage. Do not switch to `No WAL` for persistent data just to hide large-value cost. Use `No WAL` only for cache, temporary, or intentionally rebuildable data.

## Value Mutability

Large values are often reference types. Treat stored values as immutable snapshots.

Do not insert a mutable object and then mutate it in place. If the object is still in an in-memory segment, the mutation can change the visible value without a new WAL record, without a new operation index, and without predictable recovery behavior.

Prefer:

* immutable records or classes,
* serialized payloads,
* clone-and-upsert updates,
* compact structs when the value is small enough.

See [value mutability](../concepts/value-mutability.md).

## Reads

Large values make point reads and range scans more expensive.

For repeated nearby reads, block cache behavior matters. Keep a maintainer alive and tune `BlockCacheLifeTime` if repeated reads benefit from cached decompressed blocks.

For one-off scans, keep iterator block cache contribution disabled so the scan does not fill the shared block cache with data that will not be read again.

```csharp
using var iterator = zoneTree.CreateIterator(
    contributeToTheBlockCache: false);
```

Use `contributeToTheBlockCache: true` only when the scan represents a useful working set.

## Symptom Guide

| Symptom | Likely pressure | First actions |
| --- | --- | --- |
| Memory grows quickly during inserts | too many large values in mutable/read-only memory | lower `MutableSegmentMaxItemCount`; keep maintenance active |
| Read-only segments accumulate | large values make merge slower than writes | lower mutable segment size carefully; inspect merge duration and storage throughput |
| Merges are slow | large payloads increase merge IO and serialization cost | reduce value size; lower multipart part counts; tune compression |
| Disk segment becomes too large | `DiskSegmentMaxItemCount` is too high for payload size | lower `DiskSegmentMaxItemCount` |
| WAL files are large | values are large or poorly compressible | test compressed WAL with real payloads; reduce payload size if possible |
| Random reads are expensive | large compressed blocks or large serialized values | tune disk compression block size; split metadata from payload |
| Process memory stays high after reads | decompressed block cache retains large blocks | shorten `BlockCacheLifeTime`; check iterator behavior |

## Practical Model

For large values, tune by bytes and access pattern:

* lower `MutableSegmentMaxItemCount`,
* lower multipart `MinimumRecordCount` and `MaximumRecordCount`,
* consider lower `DiskSegmentMaxItemCount`,
* benchmark compression with real values,
* tune disk compression block size for read behavior,
* keep block cache lifetime aligned with memory budget,
* store references instead of payloads when values are too large for the desired operational shape.
