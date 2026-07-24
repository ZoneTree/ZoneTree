# Troubleshooting

Use this page after collecting the basic signals from [diagnostics](diagnostics.md). Each section maps a symptom to the most likely subsystem and the first useful actions.

## Database Does Not Open

Common causes:

* wrong data or WAL directory,
* `Open()` used for a missing database,
* `Create()` used for an existing database,
* key/value type mismatch,
* comparer or serializer type mismatch,
* incompatible persisted comparer behavior,
* with Bloom filtering enabled, a case-insensitive string comparer paired with
  an incompatible hasher,
* metadata or WAL corruption.

First actions:

* open with the same key type, value type, comparer, and serializers used when the database was created,
* verify data and WAL paths,
* check the first metadata or WAL error in logs,
* restore from backup if corruption is confirmed.

## Writes Slow Down

Likely pressure:

* WAL mode or storage latency,
* serializer or value-copy cost,
* large values,
* mutable segment moving too often,
* read-only segments accumulating,
* unnecessary atomic or transactional API use.

First actions:

* if maintenance is part of the workload, verify that its maintainer or custom
  event-driven maintenance policy is running as intended,
* use `Upsert` for simple inserts and replacements,
* use atomic methods only when the new value depends on the current value,
* use transactions only when several keys must change together,
* tune `MutableSegmentMaxItemCount` by expected record byte size,
* benchmark WAL modes with the real storage device.

See [write amplification](../tuning/write-amplification.md), [large values](../tuning/large-values.md), and [WAL modes](../durability/wal-modes.md).

## Read-Only Segments Accumulate

Likely pressure:

* no maintainer is running,
* merge work is slower than the write rate,
* another merge is already running,
* read-only segments are not fully frozen yet,
* merge was cancelled or failed.

First actions:

* create a maintainer for automatic merging, or verify that the custom
  maintenance controller observes state/events and starts merges as intended,
* inspect `OnMergeOperationEnded`,
* lower mutable segment size if memory pressure is high,
* inspect merge failures in logs.

Useful merge results:

| Result | Meaning |
| --- | --- |
| `SUCCESS` | merge completed |
| `RETRY_READONLY_SEGMENTS_ARE_NOT_READY` | maintainer can retry |
| `ANOTHER_MERGE_IS_RUNNING` | another merge is active |
| `NOTHING_TO_MERGE` | no read-only segments were ready |
| `CANCELLED_BY_USER` | cancellation was requested |
| `FAILURE` | check logger |

## Reads Slow Down

Likely pressure:

* too many segments must be searched,
* key layout does not match the read pattern,
* sparse array density is too low,
* decompressed block cache lifetime is too short for the working set,
* key/value circular caches are too small for repeated same-record reads,
* mutable-segment Bloom filtering is disabled or has a high false-positive rate,
* materialized-entry chunks have little reuse,
* sequential search hints do not match the actual access order,
* iterator prefetch is too small or too large for the range shape,
* compression block size is too large for random reads.

First actions:

* keep maintenance healthy,
* design keys around range scans and locality,
* tune `DefaultSparseArrayStepSize` after measuring seeks and point reads,
* verify the comparer and hasher contract before interpreting Bloom behavior,
* tune `BlockCacheLifeTime` for repeated nearby disk reads,
* benchmark `MaterializedEntryCacheSize` and `SearchHintPrefetchSize` separately,
* benchmark iterator prefetch with the real query limit,
* review disk compression block size for random-read workloads.

See [read-path caching](../tuning/read-path-caching.md), [key ordering](../concepts/key-ordering.md), and [disk segment tuning](../tuning/disk-segments.md).

## Process Memory Looks High

High process memory does not always mean ZoneTree is holding that much live data. .NET may keep freed memory available for reuse.

Likely ZoneTree contributors:

* mutable segment,
* read-only segments waiting for merge,
* large keys or values,
* decompressed disk block cache,
* circular key/value caches,
* mutable-segment Bloom filters,
* materialized key/value chunks attached to decompressed blocks,
* thread-local search-hint buffers,
* iterator prefetch buffers,
* long-lived iterators,
* temporary merge and WAL buffers.

First actions:

* measure live managed objects with .NET diagnostics,
* tune `MutableSegmentMaxItemCount` by expected byte size,
* keep maintenance running,
* shorten `BlockCacheLifeTime` if inactive disk blocks are retained too long,
* dispose iterators promptly,
* review value shape and value size.

See [memory usage](../storage/memory-usage.md).

## Disk Usage Grows

Likely pressure:

* old record versions are waiting for merge,
* deletion markers or TTL-expired records are waiting for compaction,
* bottom segments are accumulating,
* WAL files still contain unmerged in-memory data,
* backup generations are retained,
* old segment or WAL files could not be dropped.

First actions:

* inspect merge and bottom-merge activity,
* inspect failed drop events,
* check WAL directory size,
* check backup retention,
* keep logs for the underlying file-system exception.

Failed drop events mean a cleanup attempt threw after ZoneTree had already moved the logical tree shape forward. Investigate the storage/provider exception and clean up obsolete files when appropriate.

## Deleted Records Still Appear

Normal read APIs hide deleted records.

Deleted records can appear when:

* using scans with `includeDeletedRecords: true`,
* inspecting old segment files,
* data has not yet been compacted.

First actions:

* use normal read APIs for live-record semantics,
* reserve `includeDeletedRecords: true` for diagnostics, backup, restore, replication, or low-level tooling,
* keep maintenance running so obsolete records can be removed during compaction.

## Count Looks Higher Than Expected

`TotalRecordCount` is a physical storage-shape counter, not a unique live-record count. The same key can exist in multiple layers until compaction removes older versions.

Use:

* `Count()` or `CountFullScan()` for live-record counts,
* maintenance counters for storage pressure diagnostics.

## Recovery Is Slow

Likely pressure:

* large WAL files,
* many unmerged read-only segments,
* slow storage,
* compression cost,
* single-segment garbage collection work.

First actions:

* keep maintenance healthy before shutdown,
* wait for background work during controlled shutdown,
* avoid very large mutable segments unless memory and recovery time are acceptable,
* review WAL mode and compression settings.

## Recovery Fails

Common causes:

* metadata type/comparer/serializer mismatch,
* incompatible database version,
* meta WAL corruption,
* segment WAL corruption,
* missing segment files,
* storage/provider path mismatch.

First actions:

* reopen with the same factory configuration,
* verify data and WAL directories,
* check logs for the first WAL or metadata error,
* restore from backup if WAL corruption is confirmed.

## Transactions Are Slow Or Stay Pending

Likely pressure:

* transactions are long-lived,
* transactions depend on pending transactions,
* abandoned transaction ids remain in the transaction log,
* auto-commit helpers are used for paths that do not need transaction logging.

First actions:

* keep transactions short,
* use non-transactional `Upsert` for simple writes,
* use atomic methods for same-key read-modify-write,
* inspect `CommitResult.IsPendingTransactions`,
* roll back abandoned transaction ids with `RollbackUncommittedTransactionIdsBefore(...)` during operational cleanup.

See [transactions](../usage/transactions.md).

## Live Backup Generation Is Skipped

Live backup allows one generation at a time. Scheduled and merge-triggered requests are opportunistic; if a generation is already running, the new request is skipped.

First actions:

* inspect generation duration,
* reduce backup destination latency,
* lower file transfer concurrency if the destination is overloaded,
* use `CreateGenerationAsync()` when the caller must see a busy failure.

## Live Backup Restore Fails Because Target Exists

Restore expects an empty target ZoneTree directory. If the target already contains ZoneTree metadata, restore throws `LiveBackupRestoreTargetAlreadyExistsException`.

First actions:

* restore into a fresh data directory,
* remove the old target outside ZoneTree only when that is intentional.

## Local Backup Retention Does Not Delete A Segment File

Segment files are immutable and may be reused by newer generations. Local retention deletes a file only when no retained generation references its backup path.

First action:

* check retained generation catalogs before treating the file as leaked.

## Custom Backup Store Behaves Inconsistently

When `MaxConcurrentFileTransfers` is greater than `1`, segment-file operations can run concurrently.

First actions:

* make the custom store safe for concurrent segment-file operations,
* or configure `MaxConcurrentFileTransfers = 1`.

## Restore From Custom Source Has Missing Or Wrong Segments

Restore uses the `Order` value of each `DiskSegmentFile` to rebuild the `DiskSegment` and bottom segment layout.

First action:

* preserve file order exactly in custom backup sources.
