# Diagnostics

ZoneTree diagnostics start with the storage shape: mutable segment, read-only segments, `DiskSegment`, bottom segments, WAL, caches, and maintenance activity.

Use this page to decide what to measure before tuning or troubleshooting.

## Core Counters

Maintenance exposes the fastest view of the current LSM shape.

| Counter | What It Tells You |
| --- | --- |
| `MutableSegmentRecordCount` | records currently in the writable mutable segment |
| `ReadOnlySegmentsCount` | frozen in-memory segments waiting for merge |
| `ReadOnlySegmentsRecordCount` | records waiting in read-only in-memory segments |
| `InMemoryRecordCount` | mutable plus read-only segment records |
| `TotalRecordCount` | physical records across segment layers |
| `IsMerging` | normal merge is running |
| `IsBottomSegmentsMerging` | bottom segment merge is running |
| `BottomSegments.Count` | number of bottom disk segments |

`TotalRecordCount` is a physical storage-shape counter. It can include older versions and deletion markers until merge removes them. Use `Count()` or `CountFullScan()` when you need live-record count.

## Segment Movement Events

Events are the cleanest way to log segment movement without polling.

| Event | Use |
| --- | --- |
| `OnMutableSegmentMovedForward` | mutable segment became read-only |
| `OnMergeOperationStarted` / `OnMergeOperationEnded` | normal merge timing and result |
| `OnBottomSegmentsMergeOperationStarted` / `OnBottomSegmentsMergeOperationEnded` | bottom merge timing and result |
| `OnDiskSegmentCreated` | new disk segment files were created |
| `OnDiskSegmentActivated` | new disk segment became part of the tree shape |
| `OnCanNotDropReadOnlySegment` | cleanup could not drop a read-only segment |
| `OnCanNotDropDiskSegment` | cleanup could not drop a disk segment |
| `OnCanNotDropDiskSegmentCreator` | temporary merge output cleanup failed |

```csharp
zoneTree.Maintenance.OnMergeOperationEnded += (_, result) =>
{
    Console.WriteLine($"Merge ended: {result}");
};

zoneTree.Maintenance.OnMutableSegmentMovedForward += tree =>
{
    Console.WriteLine(
        $"read-only segments={tree.ReadOnlySegmentsCount}, " +
        $"in-memory records={tree.InMemoryRecordCount}");
};
```

Failed drop events mean ZoneTree could not delete obsolete segment files, WAL files, or temporary merge output after the logical tree shape had moved forward. Keep the exception details and investigate the storage/provider error; the event usually indicates cleanup debt rather than a corrupted active tree.

## Logger Signals

Configure a logger in production and retain logs around:

* failed merges,
* failed drops,
* WAL read errors,
* recovery warnings,
* live backup failures,
* unusually long maintenance operations.

```csharp
using ZoneTree.Logger;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .SetLogger(new ConsoleLogger(LogLevel.Info))
    .OpenOrCreate();
```

## Write Pressure

Watch these when write throughput or memory changes:

| Signal | Meaning |
| --- | --- |
| rising `MutableSegmentRecordCount` | current mutable segment is filling |
| rising `ReadOnlySegmentsCount` | maintenance is not merging as fast as segments move forward |
| long merge duration | merge IO, compression, serialization, or payload size is expensive |
| large WAL files | in-memory segments are not yet merged, or WAL history is intentionally retained |

Useful context:

* `MutableSegmentMaxItemCount`,
* value size,
* WAL mode,
* serializer cost,
* storage write throughput,
* maintainer settings.

## Read Path

For disk reads, inspect:

| Signal | Meaning |
| --- | --- |
| segment counts | how many layers may be searched |
| `DefaultSparseArrayStepSize` | sparse index density |
| `BlockCacheLifeTime` | how long inactive decompressed disk blocks stay cached |
| `InactiveBlockCacheCleanupInterval` | how often inactive cache cleanup runs |
| disk compression block size | random-read granularity and cache unit size |
| iterator cache contribution | whether scans populate the shared block cache |
| circular key/value cache settings | repeated same-record key/value reuse |

For compressed disk segments, decompressed block cache behavior is usually more important than circular key/value caches.

See [read-path caching](../tuning/read-path-caching.md).

## Memory

OS process memory is not the same thing as live ZoneTree data. .NET may keep freed memory available for reuse.

Measure:

* live managed object size,
* allocation rate,
* large object heap usage,
* retained references,
* iterator lifetimes,
* mutable/read-only segment sizes,
* decompressed block cache lifetime.

Common ZoneTree levers:

* `MutableSegmentMaxItemCount`,
* value size,
* maintainer cleanup,
* `BlockCacheLifeTime`,
* iterator lifetime.

## WAL And Recovery

Track:

* WAL directory size,
* recovery duration,
* incomplete WAL tail reports,
* checksum or deserialization failures,
* serializer/comparer compatibility.

An incomplete tail after process termination is a normal recovery boundary. Checksum and deserialization failures are integrity signals and should be investigated.

See [recovery](../durability/recovery.md) and [WAL modes](../durability/wal-modes.md).

## Backup

For live backup, measure:

* generation duration,
* failed generation logs,
* file transfer duration,
* record batch size,
* local retention behavior,
* restore test results.

Live backup is generation based. A generation contains disk segment files and optional in-memory records for that backup point.

See [backups](../durability/backups.md).

## Benchmark Shape

When recording benchmark or incident data, include:

* key and value type,
* serializers,
* comparer,
* WAL mode,
* disk segment mode,
* compression settings,
* mutable segment size,
* multipart min/max record count,
* `DiskSegmentMaxItemCount`,
* sparse array step size,
* block cache lifetime,
* storage hardware,
* maintainer settings,
* backup activity.

Without the shape, numbers are hard to compare.
