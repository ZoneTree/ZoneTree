# Write Amplification

Write amplification is the extra data a storage engine writes internally while applying application writes.

ZoneTree is designed to keep that cost local. A small localized update should not require rewriting a very large persistent level. The engine does this with two dimensions:

* vertical LSM layers,
* horizontal multipart disk segments.

The vertical layers batch writes and keep the normal disk merge layer bounded. The horizontal parts bound the persistent rewrite unit inside a disk segment and allow unchanged data to be carried forward.

Three record-count controls matter most:

| Option | Default | Role |
| --- | ---: | --- |
| `MutableSegmentMaxItemCount` | `1_000_000` | controls when the mutable segment moves to read-only memory |
| `DiskSegmentMaxItemCount` | `20_000_000` | controls when `DiskSegment` moves to bottom segments |
| `MinimumRecordCount` / `MaximumRecordCount` | `1_500_000` / `3_000_000` | controls multipart part sizes inside a disk segment |

## Vertical Layers

New writes enter the mutable segment in memory.

```text
mutable segment
  -> read-only segments
  -> disk segment
  -> bottom segments
```

The mutable segment absorbs writes first. When it moves forward, it becomes a read-only in-memory segment. Maintenance later merges read-only segments into persistent disk segments.

This turns many random writes into sorted merge output. The first control here is `MutableSegmentMaxItemCount`.

The default mutable segment limit is `1_000_000` records. This is a record count, not a byte limit. For compact records such as `int`/`int`, the default can be a good starting point. For long strings, JSON documents, large byte arrays, or mutable object payloads, use a lower value.

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .SetMutableSegmentMaxItemCount(100_000)
    .OpenOrCreate();
```

The second vertical control is `DiskSegmentMaxItemCount`. After read-only segments are merged into a new disk segment, ZoneTree checks the new disk segment length. If it is greater than `DiskSegmentMaxItemCount`, the new disk segment is moved to bottom segments and the `DiskSegment` slot is reset.

The default is `20_000_000` records.

```csharp
using var zoneTree = new ZoneTreeFactory<int, int>()
    .SetDataDirectory("data/app")
    .SetDiskSegmentMaxItemCount(20_000_000)
    .OpenOrCreate();
```

This keeps the normal disk merge layer from growing forever. New read-only segments continue to merge into a fresh `DiskSegment` instead of repeatedly merging into one increasingly large disk segment.

## Horizontal Parts

`MultiPartDiskSegment` is the default disk segment mode.

A multipart disk segment is one logical disk segment made from ordered immutable parts.

```text
logical disk segment
  -> part A
  -> part B
  -> part C
  -> part D
```

Each part owns a sorted key range. During merge, ZoneTree can create the next logical disk segment from both new output and existing old parts:

```text
old clean parts carried forward
+
newly written parts for changed ranges
```

This is the core write-amplification reduction. ZoneTree does not need to rewrite clean old parts just because one range changed. The changed range is rewritten as new merge output, while unrelated old parts can be included in the new logical disk segment without rewriting their records.

The old files are immutable. ZoneTree does not patch them in place. It either carries a clean part as-is or writes replacement output for the affected range.

## Bounded Rewrite Units

Part size controls the approximate maximum local rewrite unit for ordinary localized merges.

If a disk segment were one enormous part, a small update inside it could force a very large rewrite. Multipart segments avoid that by splitting the logical disk segment into bounded parts.

```text
larger parts = fewer files, larger possible local rewrite
smaller parts = more files, smaller possible local rewrite
```

This makes write amplification tunable.

Defaults:

| Option | Default |
| --- | ---: |
| `MinimumRecordCount` | `1_500_000` |
| `MaximumRecordCount` | `3_000_000` |

These options are record counts. The byte size depends on your key and value types.

## Why Randomized Part Size Matters

If every compaction emitted fixed-size parts at the maximum size, the disk segment would become tightly packed:

```text
[ 3M ][ 3M ][ 3M ][ 3M ][ tail ]
```

Now suppose a new key is inserted inside the second part.

The old part is immutable, so ZoneTree must produce new merge output for that local range. But the original part was already full. If the replacement output becomes even slightly larger, it may no longer fit into one maximum-size part.

That creates a cycle:

```text
compaction emits full parts
  -> small update lands inside a full part
  -> local output must split or reshape
  -> compaction emits full parts again
  -> the same pattern can repeat
```

ZoneTree avoids this rigid shape by choosing each multipart part target randomly between the configured minimum and maximum record counts.

Instead of always producing:

```text
[ 3M ][ 3M ][ 3M ][ 3M ]
```

ZoneTree may produce:

```text
[ 1.8M ][ 2.6M ][ 1.7M ][ 2.9M ][ 2.1M ]
```

Many parts are now below the maximum. If a later merge adds a small number of records inside one of those ranges, the replacement output can often remain below the maximum instead of being forced into an immediate split.

This is logical slack, not physical free space. ZoneTree is not appending into the old immutable file. The benefit is that the previous part was emitted below the maximum, so the next replacement part has room to absorb small local changes.

Randomized sizes also make boundaries drift over time:

```text
merge 1:
[ 0-1.8M ][ 1.8M-4.4M ][ 4.4M-6.1M ]

merge 2:
[ 0-2.3M ][ 2.3M-4.0M ][ 4.0M-6.9M ]

merge 3:
[ 0-1.6M ][ 1.6M-4.5M ][ 4.5M-7.2M ]
```

The same key ranges are less likely to be trapped forever inside the same full rewrite units.

## Example: Local Rewrite Instead Of Whole-Level Rewrite

Assume:

* `DiskSegment` is multipart,
* it contains `18M` compact records,
* it is physically represented by several ordered part files,
* one read-only segment contains `10K` new records whose sorted positions overlap part `B`.

Before merge:

```text
read-only segment:
[ 10K new records overlapping B ]

DiskSegment:
[ A ][ B ][ C ][ D ][ E ][ F ]
```

During merge:

1. ZoneTree merges the read-only segment with the old disk segment.
2. Part `A` does not overlap the new keys, so it can be carried.
3. The range around `B` is rewritten as new merge output.
4. Parts `C`, `D`, `E`, and `F` do not overlap newer keys, so they can be carried.

After merge:

```text
new DiskSegment:
[ carry A ][ write B2 ][ carry C ][ carry D ][ carry E ][ carry F ]
```

The result is still one correct ordered disk segment, but the physical write work is concentrated around the changed range.

A one-record update does not mean ZoneTree writes exactly one record to disk. It means the update can stay local to a bounded merge region instead of forcing a rewrite of the whole disk level.

If the new disk segment grows beyond `DiskSegmentMaxItemCount`, ZoneTree moves it to bottom segments:

```text
bottom segments:
[ sealed merged disk segment ]

DiskSegment:
[ empty / fresh target for future read-only merges ]
```

This is the vertical boundary. `DiskSegmentMaxItemCount` controls when `DiskSegment` is moved to bottom segments.

## Tuning Part Counts

Part counts should match record size and workload shape.

For compact data:

```csharp
using var zoneTree = new ZoneTreeFactory<int, int>()
    .SetDataDirectory("data/app")
    .OpenOrCreate();
```

The defaults of `1_500_000` to `3_000_000` records per part can be a good starting point.

For larger values:

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .ConfigureDiskSegmentOptions(options =>
    {
        options.MinimumRecordCount = 100_000;
        options.MaximumRecordCount = 250_000;
    })
    .OpenOrCreate();
```

Lower counts keep each part smaller in bytes and reduce the amount of local data that may need to be rewritten.

Tune part counts together with `DiskSegmentMaxItemCount`. The disk segment max controls the size of the `DiskSegment` layer before it moves to bottom segments. The multipart min/max controls the internal part size of that disk segment.

Use larger part counts when:

* file count is too high,
* values are compact,
* sequential scan behavior matters more than fine-grained reuse.

Use smaller part counts when:

* values are large,
* merge duration is too high,
* backup generations take too long,
* small changed ranges rewrite too much data.

Avoid setting `MinimumRecordCount` too low for normal workloads. A value such as `1` can make multipart segments too fragmented. ZoneTree may create or carry many tiny parts, which reduces the local rewrite size but increases file count, metadata overhead, iterator transitions, backup overhead, and sequential scan cost.

The minimum exists to keep the horizontal disk shape healthy. Lower it when records are large or when fine-grained merge locality matters more than file-count efficiency. Keep it higher when records are compact and scans or operational simplicity matter.

## Workload Shape

Multipart reuse is most effective when incoming records touch a limited part of the keyspace. If a read-only segment contains random keys spread across most parts, then most parts become affected and the merge may approach a full rewrite of the disk segment.

Randomized part sizes do not eliminate that fundamental cost. Their role is to prevent compaction from repeatedly recreating rigid, fully packed part boundaries when local ranges are rewritten.

## Practical Model

Think about ZoneTree write amplification this way:

```text
vertical layers
  batch writes, convert them into sorted merge output,
  and move large sealed disk segments to bottom segments

horizontal parts
  bound the local rewrite unit

part reuse
  carries clean old ranges forward without rewriting records

randomized part size
  avoids permanently full, rigid rewrite boundaries
```

For write-heavy systems, tune these together:

* `MutableSegmentMaxItemCount`,
* `DiskSegmentMaxItemCount`,
* `MinimumRecordCount`,
* `MaximumRecordCount`,
* compression,
* maintenance behavior.

These settings describe one pipeline. The best values depend on record size, write locality, read behavior, file count, and merge duration.
