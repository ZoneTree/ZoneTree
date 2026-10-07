# Segments

A segment is an ordered record set.

ZoneTree uses segments as the movement units of the storage engine. Writes start in a writable memory segment, frozen memory segments are merged into disk segments, and large disk segments can move into the bottom segment queue.

```text
writes
  |
  v
mutable segment
  |
  | move forward
  v
read-only segment queue
  |
  | merge
  v
DiskSegment
  |
  | exceeds DiskSegmentMaxItemCount
  v
bottom segment queue
```

## Storage Layers

| Layer | Code shape | Meaning |
| --- | --- | --- |
| Mutable segment | `IMutableSegment` | Writable in-memory segment protected by a WAL. |
| Read-only segments | `IReadOnlySegment` queue | Frozen in-memory segments waiting for merge. |
| `DiskSegment` | `IDiskSegment` slot | Persistent disk segment used by normal merge. |
| Bottom segments | `IDiskSegment` queue | Persistent disk segments below the `DiskSegment` slot. |

## Disk Segment Shapes

In code, `IDiskSegment<TKey, TValue>` is the important abstraction. It means "a persistent segment ZoneTree can read, merge, back up, and drop."

```text
IDiskSegment
  |
  +-- single-part disk segment
  |
  +-- multipart disk segment
```

| Term | Meaning |
| --- | --- |
| Disk segment | Persistent ordered segment. |
| Single-part disk segment | One physical disk segment layout. |
| Multipart disk segment | One logical disk segment made from ordered parts. |
| Part | Reusable disk segment inside a multipart disk segment. |

Single-part disk segments have four physical variations. ZoneTree chooses the variation from the key and value shape:

| Variation | Used when |
| --- | --- |
| Fixed-size key + fixed-size value | both key and value have fixed unmanaged size |
| Fixed-size key + variable-size value | key has fixed unmanaged size, value is variable-size |
| Variable-size key + fixed-size value | key is variable-size, value has fixed unmanaged size |
| Variable-size key + variable-size value | both key and value are variable-size |

These variations let compact unmanaged keys or values use simpler disk layouts, while strings, arrays, reference types, and other variable-size serialized values use header/offset layouts.

## Single-Part And Multipart

A single-part disk segment owns its physical files directly.

```text
single-part disk segment
  segment id: 10
  files:
    10.data.z
    10.data.h.z
    10.sparse.z
```

A multipart disk segment owns a small descriptor and points to ordered disk-segment parts.

```text
multipart disk segment
  segment id: 20
  descriptor:
    20.multi
  parts:
    segment id: 11
    segment id: 12
    segment id: 13
```

From the outside, both are one `IDiskSegment`. They expose one logical record count, one ordered keyspace, and the same read/merge/drop surface.

## Why Multipart Exists

Multipart disk segments let ZoneTree carry unchanged parts forward during merge.

```text
old disk segment:
[ A ][ B ][ C ][ D ]

new records overlap B

new disk segment:
[ carry A ][ write B2 ][ carry C ][ carry D ]
```

The old part files are immutable. ZoneTree does not patch them in place. A clean part can be reused by the next logical disk segment, while changed ranges are written as new parts.

## Null Disk Segment

`NullDiskSegment<TKey, TValue>` is an empty placeholder for the `DiskSegment` slot.

It appears when there is no disk segment in that slot, for example after a large disk segment moves into the bottom segment queue.

```text
DiskSegment slot:
  NullDiskSegment

bottom segment queue:
  disk segment 101
  disk segment 102
```

## Common Names

| Name | Refers to |
| --- | --- |
| `MutableSegmentMaxItemCount` | record count limit before the mutable segment moves forward |
| `DiskSegmentMaxItemCount` | record count limit before `DiskSegment` moves to bottom segments |
| `ReadOnlySegmentsCount` | frozen in-memory segment count |
| `MutableSegmentRecordCount` | records in the writable memory segment |
| `ReadOnlySegmentsRecordCount` | records across frozen memory segments |
| `TotalRecordCount` | physical records across mutable, read-only, disk, and bottom segment layers |

For the full storage flow, see [LSM Tree](lsm-tree.md). For multipart merge behavior, see [write amplification](../tuning/write-amplification.md).
