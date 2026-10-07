# Configuration Reference

`ZoneTreeFactory<TKey, TValue>` owns the configuration used to create or open a
tree. Configure components and options before calling an open method.

## Factory Entry Points

| Purpose | API |
| --- | --- |
| data and WAL location | `SetDataDirectory`, `SetWriteAheadLogDirectory` |
| key behavior | `SetComparer`, `SetKeyHasher`, `SetKeySerializer` |
| value behavior | `SetValueSerializer`, deletion delegates |
| mutable segment | `SetMutableSegmentMaxItemCount`, `SetMutableSegmentBloomFilterBitsPerItem` |
| persistent segments | `SetDiskSegmentMaxItemCount`, `SetDiskSegmentCompressionBlockSize` |
| grouped options | `Configure`, `ConfigureWriteAheadLogOptions`, `ConfigureDiskSegmentOptions` |
| providers | `SetRandomAccessDeviceManager`, `SetWriteAheadLogProvider`, `SetTransactionLog` |
| opening | `Create`, `Open`, `OpenOrCreate`, transactional variants |

Known key/value types receive default serializers and, where applicable,
comparers and key hashers. Custom types require explicit compatible components;
a key hasher is required only when the mutable-segment Bloom filter is enabled.

## Core Defaults

| Option | Default | Validated range or meaning |
| --- | ---: | --- |
| `MutableSegmentMaxItemCount` | `1_000_000` | at least `1_000` |
| `MutableSegmentBloomFilterBitsPerItem` | `8` | `0..64`; `0` disables |
| `DiskSegmentMaxItemCount` | `20_000_000` | at least `10_000` |
| mutable B+Tree lock mode | `NodeLevelMonitor` | defined `BTreeLockMode` value |
| mutable B+Tree node size | `128` | at least `16` |
| mutable B+Tree leaf size | `128` | at least `16` |
| single-segment garbage collection on load | disabled | boolean |
| unsafe numeric option values | disabled | boolean; does not bypass required-component or enum validation |

The public property identifiers `BTreeLockMode`, `BTreeNodeSize`, and
`BTreeLeafSize` configure ZoneTree's mutable B+Tree.

## Key Components

| Component | Contract |
| --- | --- |
| `Comparer` | defines key equality and total order |
| `KeyHasher` | comparer-equal keys must hash equally |
| `KeySerializer` | defines persisted key bytes |
| `ValueSerializer` | defines persisted value bytes |
| deletion delegates | define and create deletion markers |

## Mutable-Segment Bloom Filter

The filter is sized from `MutableSegmentMaxItemCount` and
`MutableSegmentBloomFilterBitsPerItem`. Allocation rounds up to a power of two
and is capped at `2^30` bits.

```csharp
using var zoneTree = new ZoneTreeFactory<long, string>()
    .SetMutableSegmentMaxItemCount(500_000)
    .SetMutableSegmentBloomFilterBitsPerItem(8)
    .OpenOrCreate();
```

Use `0` bits per item to disable the filter and remove the key-hasher
requirement. A larger value is not free: it increases memory and does not
eliminate comparer-based lookup after a possible match.

See [mutable-segment Bloom filters](../concepts/bloom-filters.md) for sizing,
false-positive behavior, and hasher requirements.

## WAL Defaults

| Option | Default | Validated range or meaning |
| --- | ---: | --- |
| `WriteAheadLogMode` | `AsyncCompressed` | defined mode |
| `CompressionBlockSize` | `256 KB` | `256 KB..16 MB` |
| `CompressionMethod` | `Zstd` | compatible method/level pair |
| `CompressionLevel` | Zstd level `0` | method-specific |
| async empty-queue poll interval | `100 ms` | non-negative |
| sync-compressed tail writer | enabled | boolean |
| sync-compressed tail writer interval | `500 ms` | non-negative |
| incremental backup | disabled | used by transactional-log compaction |

WAL options apply when new WALs are created. Existing WALs retain their stored
options. The modes have different caller acknowledgment and failure boundaries;
read [WAL modes](../durability/wal-modes.md) before changing them.

## Disk-Segment Defaults

| Option | Default | Validated range or meaning |
| --- | ---: | --- |
| `DiskSegmentMode` | `MultiPartDiskSegment` | defined mode |
| `CompressionBlockSize` | `4 MB` | `1 MB..64 MB` |
| `CompressionMethod` | `Zstd` | compatible method/level pair |
| `CompressionLevel` | Zstd level `0` | method-specific |
| `MinimumRecordCount` | `1_500_000` | at least `1_000` |
| `MaximumRecordCount` | `3_000_000` | at least `2_000`; must exceed minimum |
| `DefaultSparseArrayStepSize` | `1024` | non-negative; `0` disables |
| `KeyCacheSize` | `1024` | non-negative; `0` disables |
| `ValueCacheSize` | `1024` | non-negative; `0` disables |
| key cache record lifetime | `10_000 ms` | non-negative |
| value cache record lifetime | `10_000 ms` | non-negative |
| `MaterializedEntryCacheSize` | `4096` chunks/block | non-negative; `0` disables |
| `SearchHintPrefetchSize` | `16` entries | non-negative; `0` disables |

Materialized chunks contain 16 aligned entries, so the default permits at most
65,536 cached materialized positions in one decompressed block. Actual memory
depends on key/value shape and accessed positions.

```csharp
using var zoneTree = new ZoneTreeFactory<long, string>()
    .ConfigureDiskSegmentOptions(options =>
    {
        options.DefaultSparseArrayStepSize = 512;
        options.MaterializedEntryCacheSize = 2048;
        options.SearchHintPrefetchSize = 16;
    })
    .OpenOrCreate();
```

The active disk-segment options are persisted in ZoneTree metadata. Individual
segment files also retain the physical format information required to read
their stored representation.

## Iterator Defaults

| Option | Default | Meaning |
| --- | ---: | --- |
| `IteratorType` | `AutoRefresh` | refresh behavior |
| `IncludeDeletedRecords` | `false` | hide deletion markers |
| `ContributeToTheBlockCache` | `false` | avoid warming shared blocks during scans |
| `DiskSegmentPrefetchSize` | `0` | values below `2` disable prefetch |

Iterator options are per iterator and are not persisted.

## Maintainer Defaults

The maintainer owns runtime jobs and cache cleanup rather than persisted tree
options. Important defaults include a one-minute inactive block lifetime and a
30-second inactive-block cleanup interval.

```csharp
using var maintainer = zoneTree.CreateMaintainer();

maintainer.BlockCacheLifeTime = TimeSpan.FromMinutes(1);
maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromSeconds(30);
```

These settings apply to a created maintainer. The `zoneTree.Maintenance` API
exposes state, events, and operations for custom maintenance policy.

## Validation

The factory validates required components, enum values, compression
compatibility, numeric ranges, multipart bounds, and a common case-insensitive
string comparer/hasher mismatch.

`AllowUnsafeOptionValues` bypasses numeric range checks for advanced or test
configurations. It does not make invalid component combinations safe and should
not be a production tuning shortcut.

## Complete Configuration Example

```csharp
using ZoneTree;
using ZoneTree.Options;
using ZoneTree.WAL;

using var zoneTree = new ZoneTreeFactory<long, string>()
    .SetDataDirectory("data/app")
    .SetMutableSegmentMaxItemCount(500_000)
    .SetMutableSegmentBloomFilterBitsPerItem(8)
    .ConfigureWriteAheadLogOptions(options =>
    {
        options.WriteAheadLogMode = WriteAheadLogMode.AsyncCompressed;
    })
    .ConfigureDiskSegmentOptions(options =>
    {
        options.DiskSegmentMode = DiskSegmentMode.MultiPartDiskSegment;
        options.MaterializedEntryCacheSize = 4096;
        options.SearchHintPrefetchSize = 16;
    })
    .OpenOrCreate();

using var maintainer = zoneTree.CreateMaintainer();
```

See [disk-segment tuning](../tuning/disk-segments.md),
[read-path caching](../tuning/read-path-caching.md), and
[key components](../concepts/serializers-and-comparers.md).
