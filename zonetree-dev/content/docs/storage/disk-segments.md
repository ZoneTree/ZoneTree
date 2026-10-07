# Disk Segment Files

Disk segments are immutable persistent record sets. This page describes their physical file shape. For terminology, see [segments](../concepts/segments.md). For sizing and merge tuning, see [disk segment tuning](../tuning/disk-segments.md).

## File Categories

ZoneTree stores disk segment data through random-access devices. The common file categories are:

| Category | Purpose |
| --- | --- |
| `.data` | serialized key/value bytes or fixed-size record data |
| `.head` | headers and offsets for layouts that need them |
| `.sparse` | optional sparse index entries |
| `.multi` | multipart disk segment descriptor |

Compression usually adds the `.z` suffix to the physical file.

```text
single-part segment 10
  10.data.z
  10.head.z
  10.sparse.z

multipart segment 20
  20.multi
  part files for segment 11
  part files for segment 12
  part files for segment 13
```

Fixed-size key/value layouts may not need a `.head` file. Variable-size layouts use headers and offsets so ZoneTree can keep random access while supporting strings, arrays, and other variable-size serialized values.

## Single-Part Layouts

Single-part disk segments choose one of four layouts from the key and value shape:

| Layout | Physical idea |
| --- | --- |
| fixed key + fixed value | records can be addressed directly in `.data` |
| fixed key + variable value | fixed keys and value offsets are stored in `.head`; value bytes live in `.data` |
| variable key + fixed value | key offsets and fixed values are stored in `.head`; key bytes live in `.data` |
| variable key + variable value | key/value offsets are stored in `.head`; key/value bytes live in `.data` |

This is a storage optimization. The logical API stays the same for all layouts.

## Multipart Descriptor

A multipart disk segment is one logical disk segment made from ordered parts. Its `.multi` descriptor stores the part segment ids and boundary key/value information needed to route reads to the correct part.

```text
20.multi
  part ids: 11, 12, 13
  first/last keys for each part
  first/last values for each part
```

The parts are regular `IDiskSegment` instances. A part can be carried forward into a later multipart segment when its key range is unchanged by a merge.

## Sparse Index File

The `.sparse` file stores selected key/value/index entries. It helps ZoneTree position disk searches without loading every key into memory.

`DefaultSparseArrayStepSize` controls sparse index density:

| Value | Effect |
| --- | --- |
| smaller step | more sparse entries, faster positioning, more memory |
| larger step | fewer sparse entries, less memory, more local search |
| `0` | disables default sparse array creation/loading |

## Immutability

Disk segment files are immutable after creation. Merge operations create new disk segment files and then drop old files when they are no longer needed.

Iterators and live backup can pin disk segments while they read or copy files. A pinned segment is not rewritten; its deletion is delayed until the pin is released.

## Restore And Backup

`IDiskSegment.GetFiles()` returns the physical files that belong to a disk segment. For multipart segments, this includes the `.multi` descriptor and every part file.

Live backup stores these immutable files and streams in-memory records separately. Restore uses the recorded file order to rebuild the `DiskSegment` and bottom segment layout.
