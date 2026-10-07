![ZoneTree Logo](https://raw.githubusercontent.com/ZoneTree/ZoneTree/main/src/ZoneTree/docs/ZoneTree/images/logo2.png)

# ZoneTree

**The engine beneath serious .NET data systems.**

ZoneTree is a high-performance storage engine for ordered, persistent data. It is built for teams creating databases, indexes, search systems, queues, event stores, local-first applications, and custom data platforms in the .NET ecosystem.

[![NuGet](https://img.shields.io/nuget/v/ZoneTree.svg)](https://www.nuget.org/packages/ZoneTree/)
[![Downloads](https://img.shields.io/nuget/dt/ZoneTree)](https://www.nuget.org/packages/ZoneTree/)
![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Platform](https://img.shields.io/badge/platform-.NET-blue.svg)
![Build](https://img.shields.io/badge/build-passing-brightgreen.svg)
[![Join Discord](https://img.shields.io/badge/Discord-Join-5865F2?logo=discord&logoColor=white)](https://discord.gg/d9aDtzVNNv)

---

## The missing layer

Modern data systems are not built on features alone.
They are built on storage layers that shape performance, reliability, and product architecture.

![2M profile store benchmark execution time](zonetree-dev/content/docs/benchmark/reference/2m/latest-execution-time.svg)

In a live 2M-profile benchmark with individual writes, secondary indexes, point reads, ordered scans, queries, and updates, ZoneTree completes the measured workload phases substantially faster than RocksDB, SQLite, and MySQL for this embedded profile-store scenario. See the full benchmark explanation and reference reports at [zonetree.dev/docs/benchmark](https://zonetree.dev/docs/benchmark/).

.NET has excellent databases and frameworks, but very few native storage engines that can be used as a foundation for building new data systems.

ZoneTree fills that layer.

It gives .NET systems a fast, programmable foundation for ordered data that must be written quickly, read predictably, persisted reliably, and shaped around the product instead of forcing the product into a fixed database model.

---

## What ZoneTree provides

ZoneTree gives you the core pieces expected from a serious storage engine:

* Ordered key-value storage
* High-throughput writes
* Persistent storage
* Write-ahead logging
* Live backup and restore
* Configurable durability/performance trade-offs
* Forward and reverse iterators
* Seekable range scans
* Atomic read-modify-write operations
* Optimistic transactions
* Custom serializers
* Custom comparers
* Background maintenance and compaction
* In-memory and disk-backed operation
* Memory-conscious operation for datasets larger than RAM
* MIT licensed open source

ZoneTree can be used directly as an ordered key-value database, or as the storage foundation for higher-level systems.

---

## Install

```bash
dotnet add package ZoneTree
```

---

## Quick start

```csharp
using ZoneTree;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/my-zone-tree")
    .OpenOrCreate();
using var maintainer = zoneTree.CreateMaintainer();

zoneTree.Upsert(1, "Hello ZoneTree");

if (zoneTree.TryGet(1, out var value))
{
    Console.WriteLine(value);
}
```

---

## Ordered iteration

ZoneTree stores keys in order. This makes it suitable for indexes, prefix layouts, time-series patterns, range scans, and custom data models.

```csharp
using var iterator = zoneTree.CreateIterator();

iterator.Seek(100);

while (iterator.Next())
{
    Console.WriteLine($"{iterator.CurrentKey}: {iterator.CurrentValue}");
}
```

Reverse iteration is also supported:

```csharp
using var iterator = zoneTree.CreateReverseIterator();

iterator.Seek(1000);

while (iterator.Next())
{
    Console.WriteLine($"{iterator.CurrentKey}: {iterator.CurrentValue}");
}
```

---

## Atomic add or update

ZoneTree provides atomic operations for read-modify-write scenarios across LSM-tree segments.

```csharp
zoneTree.TryAtomicAddOrUpdate(
    key: 42,
    valueToAdd: 1,
    valueUpdater: (ref int value) =>
    {
        value++;
        return true;
    },
    result: (in int value, long opIndex, OperationResult result) =>
    {
        Console.WriteLine($"{result}: {value} at {opIndex}");
    });
```

Plain `Upsert` is the fastest write path. Atomic methods are synchronized with other atomic methods and are intended for operations that must read, decide, and write as one logical action.

For normal concurrent writes, use the regular write APIs. They are designed for high-throughput use. Choose atomic methods only when the new value depends on the existing value.

| Need | Use |
| --- | --- |
| Set or replace a value | `Upsert` |
| Add only if the key does not exist | `TryAdd` |
| Delete a key if it exists | `TryDelete` |
| Write a deletion marker without checking existence | `ForceDelete` |
| Increment, append, compare-and-set, or update from the current value | Atomic methods |
| Coordinate changes across multiple keys | Transactions |

You can mix these write modes in the same ZoneTree. For example, hot counter keys can use atomic read-modify-write operations, while ordinary records continue to use fast `Upsert`. This lets each workflow pay only for the coordination it needs.

---

## Transactions

For coordinated multi-key changes, open a transactional ZoneTree.

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/transactional-zone-tree")
    .OpenOrCreateTransactional();

var tx = zoneTree.BeginTransaction();

zoneTree.Upsert(tx, 1, "first");
zoneTree.Upsert(tx, 2, "second");

var result = zoneTree.PrepareAndCommit(tx);

Console.WriteLine(result.Result);
```

Use transactions when your data model requires coordination across multiple keys. For simple high-throughput writes, the non-transactional API is usually the better path.

---

## Deletions

ZoneTree uses deletion markers. This is common in LSM-tree based storage engines: a delete is written first, and obsolete data is removed later during compaction.

For many primitive and nullable types, ZoneTree can provide default deletion behavior. You can also define your own delete marker.

The same mechanism can be used for TTL-style records. Store an expiration timestamp in the value, and make the delete predicate return `true` when the value has expired. ZoneTree will treat expired records like deleted records, and maintenance/compaction can remove obsolete data later.

```csharp
using var zoneTree = new ZoneTreeFactory<int, int>()
    .SetDataDirectory("data/deletable")
    .SetIsDeletedDelegate((in int key, in int value) => value == -1)
    .SetMarkValueDeletedDelegate((ref int value) => value = -1)
    .OpenOrCreate();

zoneTree.Upsert(42, 100);
zoneTree.TryDelete(42, out var opIndex);
```

If your database does not need deletions and you want to store default values normally, deletion can be disabled:

```csharp
using var zoneTree = new ZoneTreeFactory<int, int>()
    .DisableDeletion()
    .OpenOrCreate();
```

---

## Maintenance

`CreateMaintainer()` returns an optional, ready-to-use background maintenance
coordinator. It responds to ZoneTree events, starts and tracks merge operations
according to configurable policies, and periodically releases inactive read
buffers and cache entries.

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .OpenOrCreate();

using var maintainer = zoneTree.CreateMaintainer();

zoneTree.Upsert(1, "value");
```

The maintainer is caller-owned and disposable; disposal waits for its tracked
merge operations to finish. Without a maintainer, the caller is responsible for
defining and coordinating maintenance through the state, events, and operations
exposed by `zoneTree.Maintenance`.

---

## Memory usage

ZoneTree is designed for datasets larger than memory. It does not need to load the whole database into RAM.

Memory usage mainly comes from the active mutable segment, frozen in-memory segments waiting for merge, disk block cache, sparse indexes, iterators that pin active segments, and temporary buffers used during merge or WAL processing.

The most important write-side memory control is the mutable segment size. By default, ZoneTree starts moving a mutable segment toward disk after `1_000_000` records. This is a good default for small keys and values, but applications storing large strings, documents, or payload objects should lower the mutable segment record limit to keep memory usage predictable.

ZoneTree is built as a native .NET storage engine and uses the .NET garbage collector as part of its design. The GC is highly optimized for modern application workloads and helps keep ZoneTree simple, safe, and fast without requiring manual memory management.

When observing memory usage, remember that .NET may keep freed memory available for reuse instead of returning it to the operating system immediately. Process memory can remain high even after segments are merged or released, so use .NET memory diagnostics when you need to measure live ZoneTree data precisely.

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .SetMutableSegmentMaxItemCount(10_000)
    .OpenOrCreate();
```

For read-heavy workloads, memory is mostly shaped by cache behavior and the active working set. For write-heavy workloads, memory is mostly shaped by mutable segment size and how quickly maintenance can move frozen segments to disk.

---

## Durability and storage

ZoneTree persists data through disk segments and write-ahead logs. Recent writes enter the mutable segment and are protected according to the configured WAL mode. Maintenance later moves and merges data into disk segments.

ZoneTree uses async compressed WAL by default. It is the recommended starting point for most persistent databases because it keeps WAL protection enabled while preserving very high write throughput.

| Need | Consider |
| --- | --- |
| Recommended high-throughput persistent WAL mode | Async compressed WAL |
| Plain synchronous WAL write path | Sync WAL |
| Compressed WAL with a synchronous caller write path and tail durability tradeoff | Sync compressed WAL |
| Intentional no-WAL boundary for cache/temp/rebuildable data | No WAL |

Disk segments can also use compression and different segment layouts. These options help tune disk space, read patterns, merge behavior, and file-size boundaries.

For long-running services, ZoneTree also supports live backup for built-in non-transactional trees. Live backup creates complete backup generations while the tree remains open for reads and writes, and local backup generations can be restored through `ZoneTreeFactory`.

## What can be built with ZoneTree?

ZoneTree is designed from the ground up as a storage-engine foundation for scalable systems. It is not a distributed database by itself; instead, it provides the ordered keyspace, durability controls, iterators, operation indexes, transactions, and maintenance hooks needed to build partitioned, replicated, or domain-specific data platforms above it.

It can power:

* custom databases
* indexing layers
* full-text search engines
* document stores
* graph stores
* event stores
* time-series storage
* persistent queues
* local-first data layers
* specialized storage systems
* server-side data platforms

The engine provides the ordered, persistent core; your product defines the model above it.

---

## ZoneTree.FullTextSearch

[`ZoneTree.FullTextSearch`](https://www.nuget.org/packages/ZoneTree.FullTextSearch/) is a full-text search library built on ZoneTree.

It provides indexing and search capabilities for applications that need fast text search over ZoneTree-backed storage.

Repository:

* [ZoneTree.FullTextSearch](https://github.com/ZoneTree/ZoneTree.FullTextSearch)

---

## Documentation

Official documentation:

* [Docs home](zonetree-dev/content/docs/index.md)
* [Getting started](zonetree-dev/content/docs/getting-started.md)
* [Reads and writes](zonetree-dev/content/docs/usage/reads-and-writes.md)
* [Value mutability](zonetree-dev/content/docs/concepts/value-mutability.md)
* [Memory usage](zonetree-dev/content/docs/storage/memory-usage.md)
* [WAL modes](zonetree-dev/content/docs/durability/wal-modes.md)
* [Production checklist](zonetree-dev/content/docs/operations/production-checklist.md)
* [zonetree.dev](https://zonetree.dev)

---

## Official support

Building production systems on ZoneTree?

Official support is available for organizations that want direct guidance on architecture, performance, reliability, storage design, troubleshooting, and production readiness.

Support engagements can include prioritized issue handling, prioritized bug fixes for confirmed defects, private technical communication, upgrade guidance, and scheduled advisory sessions.

Start here:

* [zonetree.dev/support](https://zonetree.dev/support)

---

## Sponsorship

ZoneTree is open-source infrastructure for the .NET ecosystem.

Sponsorship helps sustain engineering work behind the project: performance, testing, documentation, benchmarks, integrations, and long-term stability.

Sponsor ZoneTree:

* [zonetree.dev/sponsor](https://zonetree.dev/sponsor)

---

## Contributing

Contributions are welcome.

Good contributions include:

* reproducible bug reports
* focused pull requests
* tests for edge cases
* benchmark improvements
* documentation fixes
* performance investigations

Before large changes, please open an issue or discussion first.

---

## License

ZoneTree is licensed under the **[MIT License](https://github.com/ZoneTree/ZoneTree?tab=MIT-1-ov-file#readme)**.
