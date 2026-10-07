# Recovery

ZoneTree recovery is built around two durable facts:

* metadata records describe the current storage shape,
* segment WALs contain records that have not yet become ordinary disk segment files.

On open, ZoneTree reconstructs the tree from those facts and then resumes writing with segment ids and operation indexes that do not move backwards.

## What ZoneTree Loads

Opening an existing tree starts with the metadata WAL. Metadata contains the key type, value type, comparer type, serializer types, current mutable segment id, read-only segment ids, `DiskSegment` id, bottom segment ids, and stored options.

The loader validates that the opened `ZoneTreeFactory<TKey, TValue>` matches the persisted shape:

* key type,
* value type,
* comparer type,
* key serializer type,
* value serializer type.

These are part of the database identity. Do not change comparer or serializer behavior for an existing database. Create a new ZoneTree and copy or rebuild the data when the persisted ordering or binary format must change.

## Metadata WAL

The JSON metadata file is not the only source of truth. ZoneTree also stores metadata operations in a metadata WAL.

During open, ZoneTree loads the last saved metadata and then replays metadata WAL records such as:

* new mutable segment,
* read-only segment enqueue/dequeue,
* new disk segment,
* bottom segment enqueue/dequeue/insert/delete,
* maximum operation index updates.

After replay, the loader saves the compacted metadata shape again. This lets ZoneTree recover from metadata changes that happened after the last full metadata save.

## Segment WALs

Mutable and read-only in-memory segments are recovered from segment WALs. Disk segments and bottom segments are loaded from their immutable segment files.

The normal startup shape is:

```text
metadata WAL -> storage shape
read-only segment WALs -> read-only in-memory segments
mutable segment WAL -> current mutable segment
disk segment files -> DiskSegment
bottom segment files -> bottom segments
```

The loader also tracks the maximum segment id from metadata, multipart segment files, read-only segments, disk segment, and bottom segments so newly created segments continue from a safe id.

## Operation Index Recovery

Operation indexes are ZoneTree's producer write sequence. WAL records carry them, and metadata preserves the high-water mark.

During recovery, ZoneTree restores that high-water mark so new writes continue with safe sequence numbers after restart, WAL compaction, or restore. Read-only segment WALs and mutable segment WALs participate in this recovery path.

See [operation indexes](../concepts/op-index.md).

## Incomplete WAL Tails

A process crash can leave an incomplete record at the end of a WAL. ZoneTree's WAL reader stops at incomplete tail records and reports them through the logger and read result. Valid records before the incomplete tail are still usable.

Checksum or deserialization failures are different from a clean incomplete tail. They are reported as WAL read errors and should be treated as data-integrity signals.

## Single-Segment Garbage Collection

`EnableSingleSegmentGarbageCollection` applies only to a narrow startup shape: no disk segment and no read-only segments. In that case, ZoneTree can rewrite the mutable segment WAL without obsolete deleted entries after loading.

This is useful for small databases that stay in a single mutable-segment-only shape. It is not a replacement for normal maintenance and merge behavior in larger stores.

## Live Backup Restore

Live backup restore does not replay the original production WAL files. Restore copies immutable disk segment files from the backup source, writes backed-up in-memory records into a restored read-only WAL, creates a fresh mutable WAL, and saves metadata for the restored shape.

Restore requires an empty target data directory. If a ZoneTree already exists at the target, restore fails with a live-backup restore exception instead of overwriting the existing database.

After restore, open the target database normally through `ZoneTreeFactory`.

## Advanced Recovery Tools

ZoneTree exposes low-level recovery building blocks intentionally. They are useful for repair tools, migration tools, backup/restore systems, diagnostics, and offline inspection.

Useful public entry points include:

* `ZoneTreeLoader<TKey, TValue>` for loading an existing tree from metadata and WAL state,
* `ZoneTreeMetaWAL<TKey, TValue>` for reading and saving metadata state,
* `DiskSegmentFactory` for opening immutable disk segment files,
* `MutableSegmentLoader<TKey, TValue>` for loading the current mutable segment from its WAL,
* `ReadOnlySegmentLoader<TKey, TValue>` for loading frozen in-memory segments from WALs,
* `IWriteAheadLog<TKey, TValue>.ReadLogEntries` for reading WAL records through the configured WAL implementation.

These APIs are low-level. Recovery tools should load the persisted metadata or provide equivalent factory configuration so the same comparer, serializers, WAL provider, random-access device manager, and options are used when reading segments and WALs.

## Production Guidance

For production systems:

* keep the default async compressed WAL unless you have a specific reason to change it,
* use Sync WAL when the application specifically needs the plain synchronous WAL path,
* use sync-compressed WAL only when its compressed-block tail durability tradeoff is acceptable,
* use `No WAL` only for cache, temporary, or intentionally rebuildable data,
* keep maintenance running for long-lived write-heavy workloads,
* dispose ZoneTree instances during graceful shutdown,
* test restart and restore with the same serializers, comparer, WAL mode, and disk options used in production,
* keep live backups when the data cannot be rebuilt from another source.

WAL recovery protects ZoneTree's local process-crash recovery boundary. Full power-loss guarantees still depend on operating system, file-system, hardware, and storage-device behavior.
