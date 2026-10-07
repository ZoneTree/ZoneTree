# Indexes

ZoneTree is a natural foundation for indexes because it stores keys in comparer order and exposes efficient seek-based iteration. An index in ZoneTree is usually a key layout plus a value shape.

## Primary Index

A primary index stores the entity by its primary id:

```text
user:{userId} -> user
```

This is the simplest shape when reads usually know the id.

```csharp
users.Upsert($"user:{user.Id:D20}", user);
```

## Secondary Index

A secondary index encodes the indexed field into the key and stores the primary id or a compact pointer as the value.

```text
email:{email} -> userId
status:{status}:created:{createdAt}:{userId} -> userId
```

The key carries the sort order. The value can stay small.

```csharp
emailIndex.Upsert($"email:{email}", userId);
statusIndex.Upsert(
    $"status:{status}:created:{createdAt.Ticks:D20}:{userId:D20}",
    userId);
```

## Composite Keys

Composite key layouts make range scans cheap:

```text
tenant:{tenantId}:status:{status}:created:{createdAt}:id:{entityId}
```

This lets you scan all records for one tenant and status in creation order.

```csharp
var prefix = $"tenant:{tenantId}:status:active:";

using var iterator = indexTree.CreateIterator();
iterator.Seek(prefix);

while (iterator.Next())
{
    if (!iterator.CurrentKey.StartsWith(prefix, StringComparison.Ordinal))
        break;

    Console.WriteLine(iterator.CurrentValue);
}
```

For structured key types, use a comparer that orders fields in the same order your scans need.

## Covering Indexes

An index value can store only the primary id:

```text
email:{email} -> userId
```

Or it can store enough data to answer the query without loading the primary record:

```text
status:{status}:created:{createdAt}:{userId} -> UserListItem
```

Covering indexes improve read paths but increase write cost and value size. Use them when the query is hot enough to justify duplicating data.

## Updating Indexes

If the primary record and secondary index entries must change together, use a transactional tree.

```csharp
using var zoneTree = new ZoneTreeFactory<string, string>()
    .SetDataDirectory("data/users")
    .OpenOrCreateTransactional();

var tx = zoneTree.BeginTransaction();

zoneTree.Upsert(tx, "user:42", userJson);
zoneTree.Upsert(tx, "email:alice@example.com", "42");

var result = zoneTree.PrepareAndCommit(tx);
```

If an index is rebuildable from primary data, keep it in a separate ZoneTree and repair it with an iterator scan. Rebuildable indexes can use a different durability policy than primary data when the product can tolerate rebuilding them.

## Rebuilds

Iterators make rebuilds straightforward:

```csharp
using var iterator = primary.CreateIterator(IteratorType.Snapshot);

while (iterator.Next())
{
    var user = iterator.CurrentValue;
    rebuiltEmailIndex.Upsert($"email:{user.Email}", user.Id);
}
```

Use a snapshot iterator when the rebuild needs a stable view. For online rebuilds, combine a snapshot pass with an application operation stream so writes that happen during the rebuild are applied after the scan.

## Partitioned Indexes

Indexes can be partitioned independently from primary data:

```text
tenant:{tenantId}:email:{email}
index:{indexName}:partition:{partitionId}:{field}:{id}
```

Independent index trees give separate maintenance, backup, restore, and placement boundaries. This is useful for large tenants, heavy secondary indexes, or indexes that can be rebuilt separately from the primary store.

## WAL Mode

The default async compressed WAL is the right starting point for persistent indexes.

Use `No WAL` only when the index is intentionally rebuildable from another source. This is common for derived projections, but it should be a product decision.
