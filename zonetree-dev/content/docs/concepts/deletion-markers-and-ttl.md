# Deletion Markers And TTL

ZoneTree represents deletion as a durable write. A delete writes a value that the configured deletion delegate recognizes as deleted. That marker hides older values for the same key until merge work can remove obsolete records.

This keeps delete operations aligned with the LSM model: the engine writes a newer fact instead of immediately rewriting older persistent segments.

## Visibility Semantics

```text
older layer: key = 42, value = Alice
newer layer: key = 42, deletion marker

visible result: key 42 is deleted
```

Point reads and normal iterators use the configured deletion delegates to hide deleted records from the visible view. Iterators can also be created with deleted records included, which is useful for backup, restore, replication, inspection, and other engine-level pipelines.

## Delete APIs

`TryDelete` checks that the key is currently visible before writing a deletion marker:

```csharp
if (zoneTree.TryDelete(42, out var opIndex))
{
    // delete marker written
}
```

`ForceDelete` writes the deletion marker directly:

```csharp
var opIndex = zoneTree.ForceDelete(42);
```

`ForceDelete` is useful when the application wants a tombstone regardless of whether an older value currently exists.

## Default Markers

ZoneTree can create default deletion delegates for common value shapes.

For many primitive value types, `default(TValue)` is the deletion marker. For `Memory<byte>`, an empty value is deleted. For reference types, `null` is deleted.

If `default(TValue)` is valid application data, configure a custom marker or disable deletion.

## Custom Markers

```csharp
using var zoneTree = new ZoneTreeFactory<int, int>()
    .SetIsDeletedDelegate((in int key, in int value) => value == -1)
    .SetMarkValueDeletedDelegate((ref int value) => value = -1)
    .OpenOrCreate();
```

Here `-1` is the tombstone, so `0` remains a normal stored value.

## TTL

TTL can be modeled as a deletion predicate over the value.

```csharp
public record struct CacheEntry(string Value, DateTime ExpiresAt);

using var zoneTree = new ZoneTreeFactory<string, CacheEntry>()
    .SetIsDeletedDelegate((in string key, in CacheEntry value) =>
        value.ExpiresAt <= DateTime.UtcNow)
    .SetMarkValueDeletedDelegate((ref CacheEntry value) =>
        value.ExpiresAt = DateTime.MinValue)
    .OpenOrCreate();
```

Expired records disappear from normal reads and normal iteration. Advanced scans can opt into deleted records when they need the raw tombstone stream, for example during backup, restore, or replication. Maintenance can remove obsolete data later.

The marker delegate receives the value by `ref`. For hot paths, that is useful: a writable struct can update only the marker field instead of replacing the whole value with a copied `with` expression. In the example above, deletion is represented by changing `ExpiresAt` directly.

TTL in this shape is a visibility rule. Applications that need expiration work to happen at a specific time can scan the relevant key range or write explicit delete markers.

## Disabling Deletion

```csharp
using var zoneTree = new ZoneTreeFactory<int, int>()
    .DisableDeletion()
    .OpenOrCreate();
```

With deletion disabled, every value is visible, including `default(TValue)`.
