# Iteration And Range Scans

ZoneTree stores keys in comparer order. Iterators expose that order for full
scans, range scans, prefix layouts, and reverse reads.

## Forward Iteration

```csharp
using var iterator = zoneTree.CreateIterator();

while (iterator.Next())
{
    Console.WriteLine($"{iterator.CurrentKey}: {iterator.CurrentValue}");
}
```

## Seek

Use `Seek` to position a forward iterator at the target when it exists, or at
the following key in configured comparer order when it does not.

With an ascending integer comparer, this example scans the half-open range
`[100, 200)`:

```csharp
using var iterator = zoneTree.CreateIterator();

iterator.Seek(100);

while (iterator.Next())
{
    if (iterator.CurrentKey >= 200)
        break;

    Console.WriteLine($"{iterator.CurrentKey}: {iterator.CurrentValue}");
}
```

The range boundaries follow the configured comparer. For structured keys, use
lower and upper keys that represent the intended range in that ordering.

## Reverse Iteration

Create a reverse iterator to scan the configured comparer order backward:

```csharp
using var iterator = zoneTree.CreateReverseIterator();

while (iterator.Next())
{
    Console.WriteLine($"{iterator.CurrentKey}: {iterator.CurrentValue}");
}
```

Use `Seek` to position a reverse iterator at the target when it exists, or at
the preceding key in configured comparer order when it does not. With an
ascending integer comparer, this example scans from `1000` down through `900`:

```csharp
using var iterator = zoneTree.CreateReverseIterator();

iterator.Seek(1000);

while (iterator.Next())
{
    if (iterator.CurrentKey < 900)
        break;

    Console.WriteLine($"{iterator.CurrentKey}: {iterator.CurrentValue}");
}
```

Reverse iteration is useful for latest-first time-series queries, descending
indexes, and high-key ranges.

## Iterator Types

ZoneTree iterators support different refresh and visibility behavior.

| Type | Behavior |
| --- | --- |
| `AutoRefresh` | Default. Scans all available segments and refreshes when the mutable segment moves forward. Later writes may appear if their key position has not already been passed. |
| `NoRefresh` | Does not automatically include segments created by a later mutable-segment move. It still includes the current mutable segment and may observe later writes whose key position has not been passed. Call `Refresh()` to collect the latest segments manually. |
| `Snapshot` | Moves the mutable segment forward when the iterator is created, then scans the resulting read-only region and persistent segments. It does not see later writes. |
| `ReadOnlyRegion` | Like `Snapshot`, but does not move the mutable segment forward. It scans only data already in the read-only region and persistent segments. |

Use the default iterator for ordinary scans. Use `Snapshot` when the scan needs
a stable view containing all writes visible when the iterator is created:

```csharp
using var iterator = zoneTree.CreateIterator(IteratorType.Snapshot);
```

Use `ReadOnlyRegion` when the scan should exclude records still in the active
mutable segment and creating the iterator must not move that segment forward.

## Deleted Records

By default, iterators return live records and hide deletion markers. Include
deleted records for inspection, diagnostics, backup, restore, or
replication workflows:

```csharp
using var iterator = zoneTree.CreateIterator(
    IteratorType.NoRefresh,
    includeDeletedRecords: true);
```

## Block Cache Contribution

Disk-segment iterator reads do not contribute to the block cache by default.
This avoids retaining the decompressed blocks touched by a scan after the
iterator moves on.

Enable cache contribution explicitly when the iterator should add decompressed
blocks to the shared block cache:

```csharp
using var iterator = zoneTree.CreateIterator(
    IteratorType.AutoRefresh,
    includeDeletedRecords: false,
    contributeToTheBlockCache: true);
```

Contributing retains the scanned blocks in memory until inactive block-cache
cleanup releases them.

## Disk Segment Prefetch

`DiskSegmentPrefetchSize` controls how many consecutive disk records an
iterator may read as one batch. The default is `0`; values below `2` disable
prefetching.

```csharp
using var iterator = zoneTree.CreateIterator(new IteratorOptions
{
    IteratorType = IteratorType.AutoRefresh,
    DiskSegmentPrefetchSize = 16
});
```

Prefetching can reduce per-record overhead during disk scans and may read beyond
an early range termination.

## Dispose Iterators

Iterators retain the segments they scan. Always dispose an iterator when its
scan is complete:

```csharp
using var iterator = zoneTree.CreateIterator();
```

Long-lived iterators can delay physical deletion of segments replaced by a
merge. Keep each iterator scoped to the scan that uses it.

See [read-path caching](../tuning/read-path-caching.md) for prefetch and block
cache tuning.
