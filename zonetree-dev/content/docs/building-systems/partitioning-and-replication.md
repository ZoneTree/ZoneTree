# Partitioning and Replication

ZoneTree is designed to be the storage engine inside a database node. Its ordered keyspace, concurrent write path, operation indexes, immutable disk segments, and lifecycle APIs give a distribution layer concrete primitives for placing, copying, and replicating data.

A node can host many ZoneTrees: tenant partitions, hash partitions, ordered ranges, indexes, or replicas. The distribution layer connects those local engines into a database and defines ownership, transport, consistency, and recovery.

A dedicated distribution layer is under active development. This guide describes the engine capabilities available today and how to compose them into that architecture.

## Architecture: nodes, partitions, and replicas

A **partition** is a logical unit of data ownership and routing. A **replica** is a copy of that partition. A **node** is the process or machine hosting one or more partition replicas.

A useful starting design gives each partition replica its own ZoneTree and data directory:

```text
                    Your distribution layer
            routing / ownership / replication / recovery
                  /                 |                 \
             Node A              Node B              Node C
          ZoneTree P0          ZoneTree P0          ZoneTree P1
          ZoneTree P1          ZoneTree P2          ZoneTree P2
```

Here P0 has copies on A and B; P1 on A and C; P2 on B and C. The placement map and replication protocol live above ZoneTree. Each ZoneTree handles local reads, writes, WAL recovery, and LSM maintenance.

This separation lets an engine instance remain focused on storage while the node service manages network requests, resource budgets, and partition placement. Multiple nodes use separate storage instances and directories; replication transfers operations or a bootstrap image between them.

| Responsibility | ZoneTree contribution | Distribution layer responsibility |
| --- | --- | --- |
| Partition storage | An ordered keyspace with configurable comparers and serializers | Partition identity, routing, placement, and ownership |
| Concurrent access | Parallel writes and reads alongside background maintenance | Request admission, workload isolation, and backpressure |
| Replication | Write operation indexes, version-aware value construction, and a replica-apply helper | Durable capture, transport, retries, epochs, and acknowledgement policy |
| Data movement | Snapshot/range iterators and backup/restore abstractions | Stream boundaries, catch-up, validation, and routing cutover |
| Consistency | Atomic operations and transactions within their local API boundaries | Replica read policy, failover, and cross-partition coordination |
| Operations | Segment lifecycle events, maintenance controls, and storage providers | Node scheduling, monitoring, capacity management, and repair |

## Choose the partition boundary

Choose a boundary that helps both access patterns and operations. Partitions can be smaller than nodes, so the distribution layer can move work without moving every dataset on a machine.

| Strategy | Useful for | Design consequence |
| --- | --- | --- |
| Tenant partitions | Isolation, independent backup/restore, tenant placement | Large tenants may need further subdivision |
| Hash partitions | Spreading unrelated keys across nodes | Range queries may need to contact several partitions |
| Ordered ranges | Range scans and locality by key | Hot ranges need splitting or redistribution |
| Time buckets | Retention, time-bounded scans, moving cold data | Recent buckets can receive most of the writes |

Keep partition identity separate from the current host. Moving partition 7 from node A to node B should change placement metadata rather than the meaning of its keys. For hash routing, define a stable hash algorithm and partition-map version; process-dependent hashes such as the default .NET string hash are unsuitable as a persisted routing contract.

Open the local replica and keep its maintainer alive for the lifetime of the hosted partition:

```csharp
using ZoneTree;

var partitionId = 7;

using var partition = new ZoneTreeFactory<int, string>()
    .SetDataDirectory($"data/node-a/partitions/{partitionId}/records")
    .OpenOrCreate();
using var maintainer = partition.CreateMaintainer();

partition.Upsert(42, "Alice");
```

A node hosting many partitions owns the lifecycle of each tree and maintainer. Budget memory, caches, merge work, and backup transfers across the whole node, rather than tuning each instance independently as if it were the only workload.

### Key ordering is part of the partition contract

ZoneTree orders keys using the configured comparer. Use key layouts that keep records needed by the same range operation adjacent. A tenant-prefixed composite key can keep a tenant's records together; a timestamp component can support time-window scans.

The comparer, serializers, deletion rules, and application value schema must agree across replicas and during export, restore, and replay. A serialized value copied to another node must retain the same meaning there. Changing ownership does not change that contract.

See [serializers and comparers](../concepts/serializers-and-comparers.md) and [indexes](indexes.md) for key-layout design.

## Write identities and ordering

The ordinary write APIs expose an operation index:

* `Upsert` returns the index assigned to the write.
* Successful `TryAdd`, `TryGetAndUpdate`, and `TryDelete` operations expose an index through their `out` argument. A call that does not write can return an index of `0`.
* `ForceDelete` returns the index assigned to its deletion marker.

The engine assigns indexes from the tree's write sequence. Mutable-segment rollover carries that sequence forward, and recovery uses persisted operation-index state to continue it after reopening the original tree. Concurrent callers can finish, publish, or deliver their writes in a different order from index assignment.

For a single producer's writes, this gives a receiver a useful freshness comparison. If a key has accepted operation 102, a delayed operation 101 for that key should not replace it.

### Keep the different positions distinct

| Identity or position | Meaning |
| --- | --- |
| Source `opIndex` | The write identity assigned by one producer tree |
| Owner epoch | The distribution layer's identity for an ownership term or producer generation |
| Stream position | A position in the distribution layer's durable operation stream |
| Applied position | A receiver checkpoint whose preceding required operations have been applied |
| Replica-local `opIndex` | The index assigned when the receiver writes into its own ZoneTree |

Independently created trees have independent write sequences. After failover or restore, the distribution layer must establish how the new producer's versions relate to the previous producer's versions. Use an ownership epoch, an authoritative log order, or another explicit version contract.

A largest-seen index also differs from an applied prefix. Receiving operations 100 and 102 does not prove that operation 101 has arrived. Preserve the distinction when advancing a stream checkpoint.

### Carry the source version in the value when needed

`Upsert(key, valueGetter)` supplies the assigned operation index while constructing the stored value. This is useful when an exported record must carry its producer version:

```csharp
using System.Text.Json;
using ZoneTree;

using var partition = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/versioned-partition")
    .OpenOrCreate();
using var maintainer = partition.CreateMaintainer();

var opIndex = partition.Upsert(42, index =>
    JsonSerializer.Serialize(new
    {
        SourceOpIndex = index,
        Name = "Alice"
    }));
```

Here the version is part of the value, so it survives a key/value export. Normal iterators expose keys and values; they do not expose the original operation index as a separate field.

The value getter runs inside the local write path, before WAL append. Keep it limited to constructing the value. Put transport, remote calls, and publication in the distribution layer's capture protocol.

## Build a recoverable replication pipeline

A replication pipeline needs a recoverable source of operations, delivery, and a receiver apply path. Decide which component owns the authoritative order before connecting these pieces.

### Distribution log first

A distribution layer can record commands in its own authoritative replicated log, then apply committed commands to ZoneTree on each node:

```text
client request
    -> owner / partition router
    -> durable distribution log
    -> apply committed operations to local ZoneTrees
    -> acknowledge according to the database's consistency policy
```

The log supplies the cluster ordering and replay position. ZoneTree supplies the local materialized state. Its local write index can still be useful, but the distribution log's commit position remains a separate concept.

This design makes it natural to rebuild a local partition from a bootstrap image plus committed log entries. It also gives the distribution layer a place to define leadership, ownership epochs, batching, and acknowledgement rules.

### Engine write first

A system can also use a ZoneTree producer write and its returned operation index as the start of replication. The capture step must make publication recoverable together with the local mutation.

For example, a transactional outbox can store application state and an outgoing-operation entry in the same transactional tree. Another integration point is a custom `IWriteAheadLogProvider`: `IWriteAheadLog<TKey, TValue>.Append` receives the key, value, and assigned operation index. A custom provider must preserve the engine's WAL recovery, ordering, replacement, and durability behavior while implementing its capture policy.

A local `Upsert` followed by an unrelated network send leaves a failure window: the process can stop after the local write but before publication. The pipeline needs a durable replay source or a reconciliation procedure for that window. A callback by itself does not close it.

For transactional producers, capture committed transaction meaning and boundaries. Individual underlying storage writes can include transaction work that has not committed yet.

The engine WAL serves local recovery. Its files can be compacted or removed as the LSM tree progresses, so a replication stream needs its own retention and consumer-checkpoint policy. `EnableIncrementalBackup` is a WAL-compaction backup option, not a ready-made subscriber or change-feed API.

### Define the operation envelope

A transported operation typically carries:

| Field | Purpose |
| --- | --- |
| Partition identity | Routes the operation to the correct local replica |
| Producer identity and owner epoch | Rejects operations from obsolete ownership terms |
| Source version and/or stream position | Orders replay and supports duplicate/stale detection |
| Operation kind, key, and value or deletion marker | Describes the state change |
| Schema version | Preserves the meaning of keys and values across deployments |
| Transaction/batch boundary, when applicable | Preserves a committed unit of work |

A receiver validates ownership and schema, applies the operation under the chosen version rules, then advances its checkpoint according to its durability policy. Transport retries can deliver an operation more than once, so duplicate handling belongs in this contract.

## Apply updates with `Replicator<TKey, TValue>`

`ZoneTree.Core.Replicator<TKey, TValue>` is a local helper for applying producer-versioned upserts. It maintains a companion `IZoneTree<TKey, long>` named `LatestOpIndexes` alongside the supplied replica tree.

For each key, `OnUpsert(key, value, opIndex)` compares the incoming producer index with the stored one:

| Incoming index | Behavior |
| --- | --- |
| No stored index | Stores the index and applies the value |
| Lower than stored | Ignores the operation |
| Equal to stored | Applies the value again |
| Greater than stored | Advances the index and applies the value |

Equal-index retries must represent the same logical operation and payload. The helper compares indexes, not payload equality.

This example demonstrates stale-write rejection and a versioned delete using the default string deletion marker:

```csharp
using ZoneTree;
using ZoneTree.Core;

using var replica = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/replica-7/records")
    .OpenOrCreate();
using var replicaMaintainer = replica.CreateMaintainer();
using var apply = new Replicator<int, string>(
    replica,
    "data/replica-7/latest-source-indexes");

apply.OnUpsert(42, "Alice", 100);
apply.OnUpsert(42, "Alice updated", 102);
apply.OnUpsert(42, "Delayed older value", 101); // ignored

Console.WriteLine(replica.TryGet(42, out var value) ? value : "deleted");
// Alice updated

apply.OnUpsert(42, null, 103); // null is the default string tombstone
apply.OnUpsert(42, "Delayed older value", 102); // still ignored

Console.WriteLine(replica.TryGet(42, out _) ? "present" : "deleted");
// deleted
```

`OnUpsert` runs synchronously in its caller. The companion tree?s atomic coordination serializes its apply callbacks, including the replica write. Scale delivery and apply across partition replicas, or implement the coordination required by another apply design. The helper manages maintenance for its companion index tree; the node still owns the supplied replica and its maintainer.

Route replica mutations through the chosen version-aware apply protocol; unversioned writes that bypass it cannot be protected by its freshness comparison.

The helper's comparison is scoped to `TKey`. It does not compare owner epochs or merge unrelated producers' clocks. Route operations through a version domain the helper can compare, and fence obsolete producers before applying their traffic.

The companion index update is synchronized with the replica write through the atomic-operation result callback. They remain writes to two separate trees, rather than one durable transaction spanning both. Recovery must restore their relationship through replay, reconciliation, or a different transactional apply design before trusting a persisted checkpoint. Receiver writes also receive their own local operation indexes; the original producer index is held in the companion tree.

## Replicate deletions as versioned state changes

A delete must participate in the same ordering and replay rules as an update. Otherwise a delayed upsert can bring a deleted key back.

`ForceDelete` writes a deletion marker even when the key is absent. That is useful for an explicit delete operation in a custom apply path. The built-in `Replicator` exposes `OnUpsert`, so a deletion can instead be transported as an upsert of a value recognized by the replica's deletion predicate, as shown above.

Calling `ForceDelete` separately from a `Replicator` does not update its companion freshness index. Keep the deletion and its accepted version in the same apply protocol.

Normal reads and iterators hide deletion markers. `includeDeletedRecords: true` exposes markers retained in the collected segments, but compaction can reclaim obsolete markers. A current-state scan is therefore not a permanent history of deletes. Preserve deletion/version information in the distribution layer for as long as delayed operations or disconnected replicas can require it, or require those replicas to bootstrap again.

Custom tombstones and TTL values must have compatible meaning on every replica. See [deletion markers and TTL](../concepts/deletion-markers-and-ttl.md).

## Export a partition or an ordered range

Snapshot iterators give a stable collected view for copying data. Creating an `IteratorType.Snapshot` iterator moves the mutable segment forward, collects read-only and persistent segments, and waits for the read-only segments to finish freezing. Writes continue into a new mutable segment while the collected view is scanned.

The following helper copies a half-open range `[startInclusive, endExclusive)` into an empty target. Both trees must use compatible ordering, serialization, and deletion rules; their owners keep their maintainers alive.

```csharp
using ZoneTree;

static void CopyRangeToEmptyTarget(
    IZoneTree<int, string> source,
    IZoneTree<int, string> target,
    int startInclusive,
    int endExclusive)
{
    using var iterator = source.CreateIterator(
        IteratorType.Snapshot,
        includeDeletedRecords: true);
    iterator.Seek(startInclusive);

    while (iterator.Next())
    {
        var key = iterator.CurrentKey;
        if (source.Comparer.Compare(key, endExclusive) >= 0)
            break;

        target.Upsert(key, iterator.CurrentValue);
    }
}
```

The iterator merges the collected layers into key order and emits the current value for each key in that view. This copies state, rather than every historical operation. `target.Upsert` assigns target-local indexes, so carry source versions in the value or transfer matching replication metadata when the apply protocol requires them.

`ReadOnlyRegion` offers a similar fixed collected view without advancing the mutable segment; it excludes records still in that segment. `AutoRefresh` is useful for live scans, but keys inserted behind its position can be missed. Use the snapshot view plus a captured operation stream for a controlled online transfer.

Dispose export iterators promptly: they pin persistent segments and can delay reclamation of files replaced by compaction. For full iterator behavior, see [iteration and range scans](../usage/iteration-and-range-scans.mdx).

### Join the export to the operation stream

A snapshot gives a stable engine view. The distribution layer must establish its relationship to a stream position before using it for catch-up.

A transfer protocol can work as follows:

1. **Retain the operation stream.** Start capture or retention early enough that writes concurrent with the export remain available.
2. **Establish a base boundary.** Through the node's write/apply gate, drain relevant in-flight work, record a stream position, and create the export view. Coordinate these steps so the relationship between the base image and the stream is known.
3. **Copy the base into an empty target.** Keep the source serving new writes after the short boundary-establishment step. Export the whole partition or the selected range.
4. **Apply the retained tail.** Replay operations after the established boundary. Finish an unversioned bulk copy before applying newer updates, or use version-aware application for both paths so an older copied value cannot overwrite a newer replayed value.
5. **Validate and cut over.** Fence the old owner, drain the final tail, verify the target has reached the required applied position, and publish the new routing/ownership version. Remove old data after the transfer's recovery policy permits it.

Bootstrap data and freshness metadata must describe the same base. A newly created `LatestOpIndexes` tree has no knowledge of versions already present in an imported replica. Establish a base replay boundary or seed compatible per-key versions before accepting overlapping or delayed operations.

## Seed a replica with live backup and restore

For whole-partition movement, live backup can transfer the immutable disk files directly and stream recent in-memory records. A generation is a complete restore unit; a store can reuse unchanged segment files across generations.

Built-in live backup supports non-transactional ZoneTrees. Transactional bootstrap needs to preserve the transaction log and its relationship to the data as well.

```csharp
using ZoneTree;
using ZoneTree.Backup;

using var source = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/source-partition")
    .OpenOrCreate();
using var sourceMaintainer = source.CreateMaintainer();

using var backup = source.CreateLiveBackup(new LiveBackupOptions
{
    Store = new LocalLiveBackupProvider("backup/partition-7"),
    InMemoryMode = LiveBackupInMemoryMode.Snapshot
});
await backup.CreateGenerationAsync();

// The restore directory must be empty.
using var restored = await new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/new-node/partition-7")
    .RestoreFromLatestLiveBackup("backup/partition-7");
using var restoredMaintainer = restored.CreateMaintainer();
```

`ILiveBackupStore` and `ILiveBackupSource` let a distribution layer supply remote transfer and storage. Preserve generation file ordering and the factory configuration required to interpret the restored records. The [backup guide](../durability/backups.md) covers stores, sources, retention, and shutdown behavior.

The backup generation does not establish a replication-stream checkpoint. Its `LastOpIndex` metadata is recorded when generation creation completes, rather than atomically with collection of the backup view. A record batch's `RecordIndex` is its enumeration position, and restore assigns local indexes when rebuilding its WAL. Establish and persist the base/catch-up relationship through the transfer protocol above, rather than using these fields as proof that every source operation through a position is present.

## Define consistency at the database layer

ZoneTree's local APIs give the node several useful choices:

| Local requirement | Engine primitive | Scope |
| --- | --- | --- |
| Independent inserts or replacements | Ordinary `Upsert` | Fast, thread-safe local writes |
| Read/decide/write on a key | Atomic methods | Synchronization with other atomic methods across the tree's LSM layers |
| Coordinated changes to several keys | Transactional ZoneTree | A transaction within that transactional tree |
| Stable export | Snapshot iterator | The collected engine view |
| Recovery of local writes | Configured WAL mode and storage provider | Local recovery according to that mode and provider |

Ordinary `Upsert` does not participate in the atomic methods' coordination lock. Use a compatible write discipline for keys whose correctness depends on atomic read-modify-write. Transactions in one tree also do not commit changes across another tree or node. See [atomic operations](../usage/atomic-operations.mdx) and [transactions](../usage/transactions.mdx).

Transactional trees expose `BeginTransaction`, `Prepare`, `Commit`, and `Rollback` as separate lifecycle operations, alongside convenience methods that combine steps. These give a coordinating layer explicit local transaction controls. In the built-in optimistic implementation, `Prepare` checks local dependencies and marks the transaction ready in memory. A distributed commit protocol must establish its own durable decision records and recovery rules around those calls. `ZoneTreeFactory.SetTransactionLog` also permits a custom `ITransactionLog<TKey, TValue>` implementation when the integration needs a different transaction-log design.

The database layer then defines when those local results become externally visible:

* **Acknowledgement:** which durable log or replicas must accept a write before success is returned.
* **Reads:** which replica may serve a request and which applied position it must have reached.
* **Failover:** how a new owner is chosen, how an old owner is fenced, and how in-flight requests are retried.
* **Recovery:** how operation replay, state, and checkpoints are reconciled after a partial apply or restart.
* **Cross-partition work:** how the application coordinates changes involving more than one partition.

Choose WAL settings together with that policy. The default is `AsyncCompressed`; local method completion and a cluster's durable acknowledgement are distinct boundaries. Sync WAL and custom providers offer different local write paths, but the distribution layer must still define the required failure guarantees. See [WAL modes](../durability/wal-modes.md).

`IsReadOnly` can support a local admission policy, but changing a flag does not drain requests already executing or fence a previous owner across machines. Ownership checks and draining belong in the node's request/apply protocol.

## Operate the node using engine lifecycle APIs

The node service can observe and schedule storage work through `IZoneTreeMaintenance<TKey, TValue>` and `IMaintainer`:

| Capability | Useful node-level integration |
| --- | --- |
| `OnMutableSegmentMovedForward` and read-only segment counts | Observe ingestion pressure and merge backlog |
| Merge start/end and bottom-segment merge events | Schedule background I/O alongside transfer and recovery work |
| `OnDiskSegmentCreated` / `OnDiskSegmentActivated` | Observe persistent layout changes and backup opportunities |
| Disk and bottom-segment access | Inspect local storage layout and resource use |
| Maintainer merge, cache cleanup, wait, and cancellation methods | Manage the lifecycle and shutdown of a hosted partition |
| `IFileStreamProvider`, WAL providers, and random-access device managers | Integrate storage implementations with the node's environment |

Treat callbacks as notifications and keep them short; enqueue expensive transfer or scheduling work rather than running it inside engine callbacks. Storage lifecycle events describe local maintenance, rather than a committed cluster operation stream.

Some maintenance record counts include multiple physical versions of a key across segments. Use them as storage-pressure measurements rather than unique-key counts. Coordinate aggregate memory and I/O budgets across hosted partitions, and release iterators and backup activity before removing a replica's files.

See [maintenance](../usage/maintenance.mdx), [file stream providers](../storage/file-stream-providers.md), and the [API overview](../reference/api-overview.md) for the individual controls.

## Implementation references

The behaviors described here can be traced to these implementations:

* [Write path and atomic callbacks](https://github.com/ZoneTree/ZoneTree/blob/main/src/ZoneTree/Core/ZoneTree.ReadWrite.cs)
* [Operation-index allocation during value construction](https://github.com/ZoneTree/ZoneTree/blob/main/src/ZoneTree/Collections/BTree/BTree.Write.OpIndex.cs)
* [Replica apply and its companion index](https://github.com/ZoneTree/ZoneTree/blob/main/src/ZoneTree/Core/Replicator.cs)
* [Local transaction prepare, commit, and rollback](https://github.com/ZoneTree/ZoneTree/blob/main/src/ZoneTree/Transactional/OptimisticZoneTree.cs)
* [Snapshot and backup segment collection](https://github.com/ZoneTree/ZoneTree/blob/main/src/ZoneTree/Core/ZoneTree.Iterators.cs)
* [Live backup generation](https://github.com/ZoneTree/ZoneTree/blob/main/src/ZoneTree/Backup/LiveBackup.cs) and [restore](https://github.com/ZoneTree/ZoneTree/blob/main/src/ZoneTree/Backup/LiveBackupRestore.cs)
