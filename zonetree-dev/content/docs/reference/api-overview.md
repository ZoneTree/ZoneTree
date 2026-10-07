# API Overview

ZoneTree's API surface is organized around one idea: an ordered durable storage engine that can be used directly or composed into larger systems.

## Factory And Opening

`ZoneTreeFactory<TKey, TValue>` configures and opens a tree.

Common entry points:

* `OpenOrCreate`
* `Create`
* `Open`
* `OpenOrCreateTransactional`
* `CreateTransactional`
* `OpenTransactional`
* `RestoreFromLatestLiveBackup`
* `RestoreFromLiveBackupGeneration`

Common configuration methods:

* `SetDataDirectory`
* `SetWriteAheadLogDirectory`
* `SetComparer`
* `SetKeySerializer`
* `SetValueSerializer`
* `SetMutableSegmentMaxItemCount`
* `SetDiskSegmentMaxItemCount`
* `Configure`
* `ConfigureWriteAheadLogOptions`
* `ConfigureDiskSegmentOptions`
* `SetRandomAccessDeviceManager`
* `SetWriteAheadLogProvider`
* `SetTransactionLog`

The factory fills known serializers and comparers for supported common types when they are not explicitly configured.

## Core Tree

`IZoneTree<TKey, TValue>` is the main non-transactional interface.

Read methods:

* `TryGet`
* `ContainsKey`
* `Count`
* `CountFullScan`

Write methods:

* `Upsert`
* `TryAdd`
* `TryDelete`
* `ForceDelete`

Successful mutating operations return an operation index. The operation index is ZoneTree's producer write sequence for replay, audit, restore, and replication pipelines.

## Atomic Methods

Atomic methods synchronize same-key read-modify-write operations across LSM-tree segments.

Important methods:

* `TryAtomicGetAndUpdate`
* `TryAtomicAdd`
* `TryAtomicUpdate`
* `TryAtomicAddOrUpdate`
* `AtomicUpsert`

Use atomic methods when the next value depends on the current value of the same key. Use regular `Upsert` for simple inserts and replacements.

## Transactions

Transactional trees coordinate multi-key changes.

Important areas:

* `BeginTransaction`
* `BeginFluentTransaction`
* read/write methods with transaction id
* `Prepare`
* `PrepareAndCommit`
* `Commit`
* `Rollback`
* `ReadCommittedTryGet`
* `UpsertAutoCommit`
* `DeleteAutoCommit`
* exceptionless transaction APIs
* fluent transaction APIs

Use transactions when several keys must change together. For one-key counters or compare-and-set behavior, atomic methods are usually the smaller tool.

## Iterators

Iterators scan keys in comparer order or reverse comparer order.

Important methods:

* `CreateIterator`
* `CreateReverseIterator`
* `Seek`
* `Next`

Iterator modes:

* `IteratorType.AutoRefresh`
* `IteratorType.NoRefresh`
* `IteratorType.Snapshot`
* `IteratorType.ReadOnlyRegion`

Useful options:

* `includeDeletedRecords`
* `contributeToTheBlockCache`

Iterators are central to range scans, prefix layouts, exports, rebuilds, backup internals, and system-building workflows.

## Maintenance

Maintenance APIs expose segment lifecycle operations, counters, and events.

Use `CreateMaintainer()` for standard background maintenance.

Important counters:

* `MutableSegmentRecordCount`
* `ReadOnlySegmentsCount`
* `ReadOnlySegmentsRecordCount`
* `InMemoryRecordCount`
* `TotalRecordCount`
* `IsMerging`
* `IsBottomSegmentsMerging`

Important operations:

* `MoveMutableSegmentForward`
* `StartMergeOperation`
* `StartBottomSegmentsMergeOperation`
* `SaveMetaData`
* `ReleaseReadBuffers`
* `ReleaseCircularKeyCacheRecords`
* `ReleaseCircularValueCacheRecords`

Important events:

* `OnMutableSegmentMovedForward`
* `OnMergeOperationStarted`
* `OnMergeOperationEnded`
* `OnBottomSegmentsMergeOperationStarted`
* `OnBottomSegmentsMergeOperationEnded`
* `OnDiskSegmentCreated`
* `OnDiskSegmentActivated`
* failed drop events.

These APIs are useful for services that want dashboards, custom maintenance windows, backup triggers, or storage-placement decisions.

## Backup And Restore

Live backup APIs create complete backup generations while a built-in non-transactional ZoneTree remains open.

Important types:

* `LiveBackup<TKey, TValue>`
* `LiveBackupOptions`
* `LiveBackupSchedule`
* `LocalLiveBackupProvider`
* `LocalLiveBackupOptions`
* `ILiveBackupStore`
* `ILiveBackupSource`

Important methods:

* `CreateLiveBackup`
* `CreateGenerationAsync`
* `Start`
* `Stop`
* `WaitForLiveBackupAsync`
* `RestoreFromLatestLiveBackup`
* `RestoreFromLiveBackupGeneration`

`ILiveBackupStore` is the write side. `ILiveBackupSource` is the restore side. The local provider implements both.

## Recovery And Low-Level Loading

ZoneTree also exposes public low-level recovery building blocks for tools and advanced systems:

* `ZoneTreeLoader<TKey, TValue>`
* `ZoneTreeMetaWAL<TKey, TValue>`
* `DiskSegmentFactory`
* `MutableSegmentLoader<TKey, TValue>`
* `ReadOnlySegmentLoader<TKey, TValue>`
* `IWriteAheadLog<TKey, TValue>.ReadLogEntries`

Use these APIs when building repair tools, migration tools, offline inspection, custom restore flows, or storage services. Load persisted metadata or provide equivalent factory configuration so the same comparer, serializers, providers, and options are used.

## Storage Extension Points

ZoneTree can be embedded into custom storage environments.

Important extension points:

* `IFileStreamProvider`
* `IRandomAccessDeviceManager`
* `IRandomAccessDevice`
* `IWriteAheadLogProvider`
* `IWriteAheadLog<TKey, TValue>`
* `ITransactionLog<TKey, TValue>`

The default factory path uses local file-system backed providers. Custom providers can redirect WALs, random-access disk segments, or backup generations into a different storage environment.

## Replication Helper

`Replicator<TKey, TValue>` is a small helper for applying upserts by operation index. It keeps a companion ZoneTree of latest operation indexes by key and uses that state to make repeated or delayed same-key updates idempotent.

It is a useful building block for replay pipelines. The application still owns transport, retries, conflict policy, and topology.
