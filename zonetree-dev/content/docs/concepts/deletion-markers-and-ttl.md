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

## Skipping Deleted Parts During Merge

The optional `IsRangeDeleted` delegate lets a merge skip an entire part of a
multipart disk segment when your application can determine that all of its
records are deleted. It can be used with any kind of data: the requirement is
that your deletion rules provide a reliable way to recognize a fully deleted part.

Multipart segments store each part's first and last key/value pairs in metadata.
ZoneTree passes those pairs to the delegate. If it returns `true`, the merge skips
the part without scanning or copying its records. The existing cleanup removes
the skipped part's files after the merge succeeds.

`IsDeleted` continues to control record visibility in reads and iterators.
`IsRangeDeleted` helps maintenance reclaim storage more efficiently using the
same deletion rules.

### Delegate Contract

The arguments are `(firstKey, firstValue, lastKey, lastValue)`. First and last
follow the configured key comparer, with both endpoints included.

- Return `true` only when **every record in the part** is deleted according to
  `IsDeleted`. ZoneTree trusts this answer and does not check the interior records.
- Return `false` when you cannot establish that guarantee. The ordinary merge
  path still handles individual records and their deletion state.
- Leave the delegate `null` to disable the optimization.

Checking that both endpoints are deleted is insufficient on its own: a live
record may lie between them. The guarantee can come from a key range covered by
your deletion rules, an ordering invariant for values, or application metadata
that establishes deletion of the whole part. The delegate must also be safe to
call concurrently from normal and bottom merge threads.

### Example: Retired Record IDs

Suppose an application permanently retires IDs from 1000 through 1999. With
ascending integer keys, any part contained entirely within that interval can be
removed, regardless of the values it stores:

```csharp
const int firstRetiredId = 1000;
const int lastRetiredId = 1999;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetIsDeletedDelegate((in int id, in string value) =>
        value == null || (id >= firstRetiredId && id <= lastRetiredId))
    .SetMarkValueDeletedDelegate((ref string value) => value = null)
    .SetIsRangeDeletedDelegate((in int firstId, in string firstValue,
        in int lastId, in string lastValue) =>
        firstId >= firstRetiredId && lastId <= lastRetiredId)
    .OpenOrCreate();
```

For a part spanning IDs 1200 through 1500, the range delegate returns `true`.
For a part spanning IDs 900 through 1200, it returns `false`; the ordinary merge
path can still remove the individual records whose IDs are retired.

This example uses keys to establish deletion. The endpoint values are also
available for rules whose guarantees depend on values or application metadata.

### When Parts Can Be Removed

ZoneTree applies the optimization only where removing deletion markers cannot
expose older values:

- In a normal merge, only parts of the existing disk segment are eligible, and
  only when the bottom layer is empty.
- In a bottom merge, only parts of the oldest selected segment are eligible, and
  only when the selection includes the oldest bottom segment.

Records from newer merge inputs are still processed normally, including records
whose keys overlap a skipped part. Existing single-file inputs use the ordinary
record merge path. Multipart inputs remain eligible when the merge writes
single-file output.

## Disabling Deletion

```csharp
using var zoneTree = new ZoneTreeFactory<int, int>()
    .DisableDeletion()
    .OpenOrCreate();
```

With deletion disabled, every value is visible, including `default(TValue)`.
