# Event Stores

Event stores fit naturally on ZoneTree because streams are ordered key ranges. A stream read is a seek followed by forward iteration.

## Stream Layout

The usual shape is:

```text
stream:{streamId}:{version} -> event
```

Use fixed-width version encoding so lexical order matches numeric order:

```text
stream:order-123:00000000000000000001
stream:order-123:00000000000000000002
stream:order-123:00000000000000000003
```

Reading one stream is direct:

```csharp
var prefix = "stream:order-123:";

using var iterator = eventTree.CreateIterator();
iterator.Seek(prefix);

while (iterator.Next())
{
    if (!iterator.CurrentKey.StartsWith(prefix, StringComparison.Ordinal))
        break;

    Apply(iterator.CurrentValue);
}
```

## Appending Events

If one stream version key is written once, `TryAdd` is a useful primitive:

```csharp
var key = $"stream:{streamId}:{version:D20}";

if (!eventTree.TryAdd(key, eventJson, out var opIndex))
{
    throw new InvalidOperationException("Event already exists.");
}
```

When the next version depends on current stream state, keep the version pointer as a separate key and update it with atomic methods, or use transactions when the append must update several keys together.

```text
stream-version:{streamId} -> latestVersion
stream:{streamId}:{version} -> event
```

## Global Order

Some event stores need both per-stream order and global order.

```text
stream:{streamId}:{version} -> event
global:{sequence} -> streamId:version
```

The global sequence can be allocated with an atomic counter. If stream event and global index must commit together, use a transactional tree.

```csharp
using var eventTree = new ZoneTreeFactory<string, string>()
    .SetDataDirectory("data/events")
    .OpenOrCreateTransactional();

var tx = eventTree.BeginTransaction();

eventTree.Upsert(tx, "stream:order-123:00000000000000000042", eventJson);
eventTree.Upsert(tx, "global:00000000000000102490", "order-123:42");

var result = eventTree.PrepareAndCommit(tx);
```

## Idempotency

Retries are normal in event pipelines. Store an idempotency key when append requests can be repeated:

```text
idempotency:{requestId} -> streamId:version
```

For single-key decisions, atomic methods are enough. For append plus idempotency plus global order, use transactions or design the pipeline so a repair process can rebuild derived indexes from the stream records.

## Snapshots And Projections

Snapshots can live in another key range or another ZoneTree:

```text
snapshot:{streamId}:{version} -> snapshot
```

Projections and read models are often rebuildable. A projection can scan event ranges with iterators and write derived state into separate ZoneTrees. The projection tree can have its own WAL mode, maintenance, backup, and retention policy.

## Retention

Event retention is a product rule. Some event stores keep events forever. Others keep only recent events plus snapshots or exported archives.

ZoneTree gives the storage primitives:

* ordered stream ranges,
* deletion markers,
* compaction through maintenance,
* partitioned trees for time or tenant boundaries,
* iterators for export and rebuild.

The event-store layer decides which events can be removed and when.

## Partitioning

Large event stores usually benefit from partitioning:

```text
tenant:{tenantId}:stream:{streamId}:{version}
bucket:{yyyyMM}:stream:{streamId}:{version}
partition:{partitionId}:stream:{streamId}:{version}
```

Partitioning keeps maintenance, backup, restore, and retention windows smaller. It also gives a natural shape for sharding event streams across multiple ZoneTrees.
