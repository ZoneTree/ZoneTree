# Key Ordering

ZoneTree stores keys in comparer order. The comparer is part of the physical database shape, not only an in-memory convenience.

Ordered keys enable range scans, prefix layouts, secondary indexes, time-series ranges, ordered queues, leaderboards, and partitioned keyspaces.

## Comparer Contract

```csharp
using ZoneTree;
using ZoneTree.Comparers;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetComparer(new Int32ComparerAscending())
    .SetDataDirectory("data/index")
    .OpenOrCreate();
```

The comparer controls lookup, iteration, sparse indexes, merge output, and disk segment ordering.

ZoneTree stores the comparer type in metadata and validates it when opening an existing database. That check prevents opening a tree with a different comparer class.

The comparison semantics themselves are also part of the contract. If a custom comparer keeps the same .NET type but changes ordering logic, culture rules, constructor state, or field order, persisted data may no longer match the runtime ordering.

To change ordering, create a new ZoneTree with the new comparer or key encoding and rebuild the data into it. If a comparer type was only renamed and the ordering is identical, handle that as a metadata migration rather than a keyspace change.

## Ordered Key Design

ZoneTree locality is comparer locality. Records are adjacent when the configured comparer sorts them adjacent.

The key can be a primitive, a fixed-size struct, a record struct, an encoded binary value, a string, or a domain type. The storage behavior comes from the comparer order, not from the field names.

Consider an event stream key:

```csharp
public readonly record struct EventKey(
    int TenantId,
    long StreamId,
    long Sequence);
```

For stream reads, the comparer should order the key by:

```text
TenantId -> StreamId -> Sequence
```

That order makes one stream contiguous:

```text
(tenant: 7, stream: 41, sequence: 1)
(tenant: 7, stream: 41, sequence: 2)
(tenant: 7, stream: 41, sequence: 3)
```

A forward scan can seek to the first requested sequence and continue until the key leaves the stream:

```csharp
using var iterator = zoneTree.CreateIterator();

iterator.Seek(new EventKey(tenantId, streamId, fromSequence));

while (iterator.Next())
{
    var key = iterator.CurrentKey;
    if (key.TenantId != tenantId || key.StreamId != streamId)
        break;

    Consume(iterator.CurrentValue);
}
```

The same fields in a different comparer order describe a different physical keyspace:

```text
Sequence -> TenantId -> StreamId
```

That order is useful for a global sequence scan, but it scatters one stream across the sequence space. Same data, different storage behavior.

Packed numeric keys, fixed-size struct keys, `Memory<byte>` keys, and string keys all follow this same rule. Choose the representation and comparer that place the ranges your system reads into contiguous comparer order.

Changing field order, comparison logic, culture rules, or binary encoding changes the keyspace. Treat that as a storage migration.

## Forward And Reverse Scans

ZoneTree supports forward and reverse iterators. Reverse iteration is useful for latest-first time-series queries, descending leaderboards, and tail scans over ordered data.

See [iteration and range scans](../usage/iteration-and-range-scans.md).
