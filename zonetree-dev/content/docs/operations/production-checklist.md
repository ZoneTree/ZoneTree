# Production Checklist

Use this checklist before putting ZoneTree behind production traffic.

## Storage

* Choose the data directory intentionally.
* Ensure the storage volume has enough free space.
* Start with the default async compressed WAL for persistent data.
* Use Sync WAL when the application specifically needs the plain synchronous WAL path.
* Use sync-compressed WAL only when its compressed-block tail durability tradeoff is acceptable.
* Use `No WAL` only for cache, temporary, or intentionally rebuildable data.
* Choose disk segment mode based on database size and operational file-size needs.
* Tune sparse arrays, block cache lifetime, and circular key/value caches only after measuring read behavior.
* Plan backup and restore before production traffic.

## Memory

* Estimate key and value sizes.
* Tune `MutableSegmentMaxItemCount` for value size.
* Keep maintenance running for long-lived write-heavy applications.
* Tune `BlockCacheLifeTime` when read-side memory or repeated disk reads matter.
* Avoid long-lived iterators unless they are deliberate.
* Use .NET diagnostics to measure live memory.

## Data Model

* Choose key layout for the dominant reads.
* Choose serializers and comparers before creating the database.
* Treat stored values as immutable snapshots.
* Prefer small immutable structs or readonly record structs when they fit.
* Define deletion or TTL behavior.
* Decide whether secondary indexes are transactional or rebuildable.

## Operations

* Test restart and recovery.
* Test backup restore from a real backup generation.
* Decide whether backups are manual, scheduled, merge-triggered, or a combination.
* Configure backup retention if local backup storage should be bounded.
* Remember that built-in live backup covers non-transactional trees; transactional trees need a separate backup strategy.
* Watch disk growth.
* Watch read-only segment accumulation.
* Watch merge duration.
* Keep logs from failed maintenance/drop operations.

## API Use

* Use `Upsert` for simple writes.
* Use atomic methods only for same-key read-modify-write.
* Use transactions only for multi-key coordination.
* Dispose trees, maintainers, and iterators.
