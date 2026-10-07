# WAL Modes

ZoneTree protects recent writes with a write-ahead log according to the configured WAL mode.

The default mode is `AsyncCompressed`. It is the normal starting point for most applications: WAL protection stays enabled and writes can remain very fast because WAL work is handled through a background path.

Compression method, compression level, and block-size details are covered in [compression](../storage/compression.md).

## Choosing A Mode

| Need | Consider |
| --- | --- |
| Recommended high-throughput persistent WAL mode | Async compressed WAL |
| Plain synchronous WAL write path | Sync WAL |
| Compressed WAL with a synchronous caller write path and tail durability tradeoff | Sync compressed WAL |
| Intentional no-WAL boundary for cache/temp/rebuildable data | No WAL |

## Sync WAL

Sync WAL writes records through the plain WAL stream before the write is considered complete. It favors simple synchronous WAL recovery semantics over write throughput.

Use it when the application specifically needs the plain synchronous WAL path and can accept lower throughput. Full power-loss durability still depends on the operating system, file system, and storage device.

## Sync Compressed WAL

Sync compressed WAL stores log records in a compressed-block WAL format. The current tail block is stored separately and can be written by the tail writer job.

Use it when WAL size or write throughput matters and you can accept weaker crash durability than Sync WAL.

In sync-compressed mode, the tail writer job is enabled by default and runs every `500 ms`.

## Async Compressed WAL

Async compressed WAL is ZoneTree's default WAL mode. It is designed as a safe high-throughput default for ordinary persistent ZoneTree databases.

Writes are logged through a background path and compressed on disk. This gives most applications the right balance of speed, WAL protection, and storage efficiency.

## No WAL

No WAL gives the fastest write path but does not protect recent in-memory writes against process termination.

Use it for caches, rebuildable indexes, temporary stores, and data that can be reconstructed from another source.

## Durability Boundary

ZoneTree's WAL protects against process-level failures according to the selected mode and flush behavior. Hardware, operating system, file-system, and storage-device behavior still matter for full power-loss guarantees.

Choose `No WAL` only when the data-loss boundary is intentional. For most persistent data, start with the default async compressed WAL and move to Sync WAL only when the application specifically needs the plain synchronous WAL path.
