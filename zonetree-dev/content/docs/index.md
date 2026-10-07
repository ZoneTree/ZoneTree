# ZoneTree Documentation

ZoneTree is a high-performance LSM-tree storage engine for .NET. It provides ordered, persistent key-value storage that can be used directly or as the foundation for databases, indexes, queues, search systems, event stores, and custom data platforms.

## Core Strengths

ZoneTree is strongest when it is treated as a storage-engine foundation: an ordered durable core for building higher-level data systems.

* Ordered keys make range scans, prefix layouts, secondary indexes, queues, and time-series layouts natural.
* The LSM-tree write path gives high-throughput persistent writes.
* Multipart disk segments reduce write amplification by keeping rewrite work local when ranges change.
* Operation indexes provide a producer write sequence for replay, audit, restore, and replication pipelines.
* Iterators, live backup, restore, transactions, and maintenance hooks make ZoneTree useful as a building block for larger data systems.

## Start Here

1. [Open a tree and run maintenance](getting-started.md).
2. Learn the [LSM-tree and segment model](concepts/lsm-tree.md).
3. Choose the right [read and write APIs](usage/reads-and-writes.md).
4. Design keys for [ordering and range scans](concepts/key-ordering.md).
5. Select a [WAL durability mode](durability/wal-modes.md).
6. Complete the [production checklist](operations/production-checklist.md).

## Core Capabilities

* Ordered keys, forward and reverse iterators, seeks, and range scans
* Concurrent individual writes and atomic same-key read-modify-write operations
* Optimistic multi-key transactions
* Write-ahead logging, compressed disk segments, recovery, and live backup
* Custom serializers, comparers, key hashers, and storage providers
* Mutable-segment Bloom filtering and layered disk-read caching
* Multipart disk segments designed to limit persistent rewrite amplification

## Use ZoneTree

* [Opening a tree](usage/opening-a-tree.md)
* [Reads and writes](usage/reads-and-writes.md)
* [Atomic operations](usage/atomic-operations.md)
* [Transactions](usage/transactions.md)
* [Iteration and range scans](usage/iteration-and-range-scans.md)
* [Maintenance](usage/maintenance.md)

## Durability

* [WAL modes](durability/wal-modes.md)
* [Recovery](durability/recovery.md)
* [Backups](durability/backups.md)

## Concepts And Storage

* [Segments](concepts/segments.md)
* [Bloom filters](concepts/bloom-filters.md)
* [Key ordering](concepts/key-ordering.md)
* [Key components](concepts/serializers-and-comparers.md)
* [Deletion markers and TTL](concepts/deletion-markers-and-ttl.md)
* [Operation indexes](concepts/op-index.md)
* [Value mutability](concepts/value-mutability.md)
* [Disk segments](storage/disk-segments.md)
* [File stream providers](storage/file-stream-providers.md)
* [Memory usage](storage/memory-usage.md)
* [Compression](storage/compression.md)

## Operate And Tune

* [Configuration reference](reference/configuration.md)
* [Read-path caching](tuning/read-path-caching.md)
* [Disk-segment tuning](tuning/disk-segments.md)
* [Write amplification](tuning/write-amplification.md)
* [Large values](tuning/large-values.md)
* [Diagnostics](operations/diagnostics.md)
* [Troubleshooting](operations/troubleshooting.md)
* [Benchmark methodology](benchmark/benchmark.md)
* [API overview](reference/api-overview.md)

## Build On ZoneTree

* [Indexes](building-systems/indexes.md)
* [Queues](building-systems/queues.md)
* [Event stores](building-systems/event-stores.md)
* [Time-series storage](building-systems/time-series.md)
* [Partitioning and replication](building-systems/partitioning-and-replication.md)

ZoneTree exposes storage-engine building blocks rather than prescribing an
application model. The engine owns ordered persistence and lifecycle safety;
the application owns its key layout, indexes, transaction boundaries,
durability requirements, and operational policy.
