# Partitioning And Replication

ZoneTree is designed to be the local storage-engine cell inside larger data systems. A single ZoneTree gives one ordered durable keyspace. Multiple ZoneTrees can be composed into tenant shards, hash partitions, time buckets, secondary indexes, replicas, or domain-specific storage groups.

The power comes from combining small, explicit primitives:

* ordered keyspaces,
* high-throughput writes that return operation indexes,
* same-key atomic methods,
* transactions for local multi-key coordination,
* iterators for export, rebuild, and range movement,
* live backup and restore abstractions,
* maintenance hooks and segment lifecycle events,
* configurable WAL and random-access storage providers.

## Partition Cells

A partition cell is usually one ZoneTree with one data directory and one keyspace contract.

Common shapes:

```text
tenant:{tenantId}                 -> ZoneTree
hash:{partitionId}                -> ZoneTree
time:{yyyyMM}                     -> ZoneTree
index:{indexName}:{partitionId}   -> ZoneTree
```

This lets each cell have independent:

* maintenance,
* live backup,
* restore,
* retention,
* placement,
* memory budget,
* disk segment tuning,
* WAL configuration.

```csharp
var tenantTree = new ZoneTreeFactory<string, byte[]>()
    .SetDataDirectory($"data/tenants/{tenantId}")
    .OpenOrCreate();
```

Partition when the boundary is useful for operations or query shape. Good reasons include tenant isolation, smaller backup windows, bounded file sets, independent rebuilds, separate retention policies, or parallel placement across machines or disks.

## Ordered Keyspace Design

Partitioning works best when the key layout matches the routing rule.

Examples:

```text
tenant:{tenantId}:user:{userId}
tenant:{tenantId}:order:{createdAt}:{orderId}
series:{seriesId}:{timestamp}
index:{status}:{createdAt}:{entityId}
```

Related records should be adjacent when the dominant read is a range scan. If the dominant operation is random lookup, choose a layout that keeps routing and lookup simple.

The comparer and serializers are part of the persisted identity of a ZoneTree. Choose them before creating the partition and keep the ordering contract stable.

## Moving Data Between Partitions

ZoneTree iterators are the natural export and rebuild path. A partitioner can create a snapshot iterator, scan a range, and write the records into another ZoneTree.

```csharp
using var iterator = source.CreateIterator(IteratorType.Snapshot);
iterator.Seek(startKey);

while (iterator.Next())
{
    if (source.Comparer.Compare(iterator.CurrentKey, endKey) >= 0)
        break;

    target.Upsert(iterator.CurrentKey, iterator.CurrentValue);
}
```

Use `IteratorType.Snapshot` when the movement needs a stable view. Use normal iterators for online rebuilds where newer writes are handled by another operation stream.

## Operation Indexes

Every successful write returns an operation index. The operation index is ZoneTree's producer write sequence.

This is useful for replication and replay:

```text
partitionId
key
value or deletion marker
opIndex
source node
```

When replaying, a consumer can process operations in producer sequence. For idempotent apply, it can also remember the latest operation index accepted for each key and ignore older same-key operations that arrive later.

ZoneTree also includes `Replicator<TKey, TValue>`, a helper that keeps a companion ZoneTree of latest operation indexes by key and applies upserts to a replica idempotently.

## Replication Pipelines

A replication pipeline usually has three parts:

```text
producer write path -> operation stream -> replica apply path
```

The operation stream can be an application log, event bus, WAL-derived tool, domain event stream, or custom transport. The apply path writes into another ZoneTree.

For idempotent replay, include:

* partition id,
* key,
* value or deletion marker,
* operation index,
* source identity,
* schema or payload version if the value format evolves.

Use `Upsert` or `ForceDelete` for simple replay. Use atomic methods when replay needs to check the latest accepted operation index for the same key. Use transactions when applying one replicated operation must update several local keys together.

## Backup-Based Movement

Live backup is another useful primitive for partitioned systems. A backup generation is a complete backup unit for one ZoneTree. It can be written through `ILiveBackupStore` and read back through `ILiveBackupSource`.

This enables:

* moving a partition to a new location,
* restoring a replica from a known generation,
* seeding a new node before applying a later operation stream,
* keeping independent backup histories per tenant or shard.

Restore is exposed through `ZoneTreeFactory`:

```csharp
using var restored = await new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/partition-7")
    .RestoreFromLatestLiveBackup(source);
```

## Maintenance And Observability

Maintenance APIs expose the storage-engine lifecycle of each partition:

* mutable segment movement,
* merge start/end,
* disk segment creation,
* disk segment activation,
* bottom segment merge start/end,
* failed drop events.

These hooks make it possible to build dashboards, custom backup triggers, partition movement workflows, and placement decisions around real storage activity.

For long-running services, keep a maintainer alive per active ZoneTree unless the application intentionally controls maintenance windows itself.

## Local Consistency

ZoneTree gives strong local primitives:

* normal thread-safe writes,
* same-key atomic operations,
* transactional trees for multi-key local coordination,
* ordered snapshot and read-only iterators,
* WAL-backed recovery.

The distributed system built above ZoneTree defines transport, cross-partition ordering, retries, conflict rules, failover, membership, and placement.

That split is the design: ZoneTree gives the durable ordered engine cell; your system composes cells into the larger platform.
