# Time-Series Storage

ZoneTree works well for time-series workloads because ordered keys make time ranges cheap to scan and LSM-style writes absorb high insert rates.

## Key Layout

Put the query boundary first, then the time component:

```text
series:{seriesId}:{timestamp} -> value
tenant:{tenantId}:series:{seriesId}:{timestamp} -> value
```

Use sortable timestamp encodings:

```text
series:sensor-7:0638403840000000000
series:sensor-7:2026-06-08T12:30:00.0000000Z
```

Fixed-width ticks are compact and sort naturally. Round-trip UTC strings are readable and also sort correctly when the format is fixed.

## Range Reads

Time range reads are seek plus forward iteration:

```csharp
var prefix = "series:sensor-7:";
var start = "series:sensor-7:2026-06-08T00:00:00.0000000Z";
var end = "series:sensor-7:2026-06-09T00:00:00.0000000Z";

using var iterator = readings.CreateIterator();
iterator.Seek(start);

while (iterator.Next())
{
    if (!iterator.CurrentKey.StartsWith(prefix, StringComparison.Ordinal))
        break;
    if (string.CompareOrdinal(iterator.CurrentKey, end) >= 0)
        break;

    Consume(iterator.CurrentValue);
}
```

## Latest-First Reads

Use reverse iteration for latest-first queries:

```csharp
var prefix = "series:sensor-7:";
var upperBound = "series:sensor-7:2026-06-08T23:59:59.9999999Z";

using var iterator = readings.CreateReverseIterator();
iterator.Seek(upperBound);

while (iterator.Next())
{
    if (!iterator.CurrentKey.StartsWith(prefix, StringComparison.Ordinal))
        break;

    Console.WriteLine(iterator.CurrentValue);
}
```

Reverse iterators are part of the core API and are useful for tails, dashboards, recent events, and descending indexes.

## Write Shape

Time-series workloads often write append-like data. ZoneTree's mutable segment absorbs those writes first, then maintenance moves them through read-only segments into disk segments.

Tune:

* `MutableSegmentMaxItemCount` for the in-memory write buffer,
* `DiskSegmentMaxItemCount` for the boundary between `DiskSegment` and bottom segments,
* multipart minimum and maximum record counts for local rewrite size,
* WAL mode for the durability boundary,
* compression block size for read behavior and storage size.

For compact numeric values, the defaults may be a good starting point. For large payloads or tags, lower mutable and multipart sizes so merge units stay reasonable.

## Retention

Retention can be modeled several ways:

* TTL-style values with custom deletion logic,
* application range deletes,
* partitioned trees by day, month, tenant, or series group,
* export then drop old partitions,
* snapshots or rollups plus deletion of raw data.

A delete writes a deletion marker. Physical cleanup happens later through maintenance and compaction.

## Partitioning

Partitioning is often the cleanest time-series boundary.

```text
data/{tenantId}/{yyyyMM}/ZoneTree
data/{seriesGroup}/{yyyyMMdd}/ZoneTree
```

This keeps file sets, backups, restores, retention, and maintenance windows smaller. It also makes it easier to move old buckets to cheaper storage or delete them as a unit.

## Aggregates And Rollups

Aggregates can live in separate key ranges or separate ZoneTrees:

```text
rollup:{seriesId}:{window}:{timestamp} -> aggregate
counter:{seriesId}:{window} -> count
```

Use atomic operations for single aggregate counters. Use transactions when several aggregate keys must be updated together.

For rebuildable rollups, scan raw data with iterators and write the derived results into a separate tree.

## Read Cache

Repeated dashboard reads can benefit from the block cache when disk segments are compressed. Iterator scans do not contribute to the shared block cache by default. Enable `contributeToTheBlockCache` only when the scan represents a useful working set that will be read again.

See [read path caching](../tuning/read-path-caching.md).
