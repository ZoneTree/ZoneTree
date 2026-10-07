# LSM Tree

ZoneTree uses a Log-Structured Merge-tree model: writes are accepted in memory, protected by the WAL, then merged into sorted persistent segments by maintenance work.

The model is optimized for write throughput, ordered reads, and controlled background compaction.

## Layer Progression

```text
mutable segment
  -> read-only segments
  -> DiskSegment
  -> bottom segments
```

| Layer | Purpose |
| --- | --- |
| Mutable segment | receives current writes |
| Read-only segments | frozen in-memory batches waiting for merge |
| `DiskSegment` | persistent segment used by normal merge |
| Bottom segments | persistent segments in the bottom segment queue |

The mutable segment is bounded by `MutableSegmentMaxItemCount`. The `DiskSegment` layer is bounded by `DiskSegmentMaxItemCount`. These are record counts, not byte limits.

## Visibility Order

Reads search newest layers first:

```text
mutable segment
  -> read-only segments
  -> DiskSegment
  -> bottom segments
```

That order gives ZoneTree its update semantics. A newer value hides an older value with the same key. A newer deletion marker hides older values with the same key.

Iterators merge the same ordered layers into a single ordered view.

## Merge Behavior

Normal merge takes read-only in-memory segments and merges them with `DiskSegment`. The output becomes the next `DiskSegment`.

If the new `DiskSegment` grows beyond `DiskSegmentMaxItemCount`, it moves to bottom segments and the `DiskSegment` slot is reset.

Bottom segment merge is separate. It compacts older sealed disk segments without changing the current mutable or read-only write path.

## Multipart Persistent Layer

ZoneTree's default disk segment is multipart. One logical disk segment can contain many ordered immutable part files:

```text
multipart disk segment
  -> part A
  -> part B
  -> part C
```

This horizontal shape matters during merge. If incoming records affect only a local key range, ZoneTree can rewrite the affected range and carry unrelated clean parts forward. The persistent rewrite unit is bounded by multipart part sizes instead of the entire disk segment.

Multipart behavior is one of the main reasons ZoneTree can keep write amplification local for workloads with key locality. The full tuning model is covered in [write amplification](../tuning/write-amplification.md).

## Tuning Axes

The main LSM controls are:

* mutable segment record count,
* `DiskSegment` record count,
* multipart part size range,
* WAL mode,
* compression,
* block cache cleanup,
* maintenance scheduling.

The practical decision is how much data to batch vertically, and how large each horizontal persistent rewrite unit should be.

See [write amplification](../tuning/write-amplification.md), [disk segment tuning](../tuning/disk-segments.md), and [read-path caching](../tuning/read-path-caching.md).
