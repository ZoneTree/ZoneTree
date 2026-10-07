# Getting Started

This guide creates a persistent ZoneTree, writes and reads a record, and scans
an ordered key range.

## Install

```bash
dotnet add package ZoneTree
```

## Open A Tree

```csharp
using ZoneTree;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/my-zone-tree")
    .OpenOrCreate();

using var maintainer = zoneTree.CreateMaintainer();
```

`OpenOrCreate()` opens the existing tree in `data/my-zone-tree`, or creates it
when it does not exist.

`CreateMaintainer()` creates ZoneTree's ready-to-use background maintenance
worker and automates merge scheduling and inactive-cache cleanup. Creating a
maintainer is optional; without one, those jobs do not run automatically.

## Write And Read

```csharp
zoneTree.Upsert(1, "Hello ZoneTree");

if (zoneTree.TryGet(1, out var value))
    Console.WriteLine(value);
```

`Upsert` inserts a new key or replaces the current value. `TryGet` returns the
newest live value for the key.

## Scan An Ordered Range

```csharp
using var iterator = zoneTree.CreateIterator();

iterator.Seek(100);
while (iterator.Next() && iterator.CurrentKey < 200)
    Console.WriteLine($"{iterator.CurrentKey}: {iterator.CurrentValue}");
```

A forward iterator returns keys in comparer order. `Seek(100)` positions it so
the next successful `Next()` returns the first key greater than or equal to
`100`.

Dispose iterators when the scan is complete.

## Shutdown

The tree, maintainer, and iterators are disposable. The `using` declarations in
the examples dispose them when their scopes end. Maintainer disposal waits for
its tracked merge threads to finish. See [maintenance](usage/maintenance.md)
when you need to configure maintenance or observe merge completion explicitly.

## Next Steps

* [Opening and configuring a tree](usage/opening-a-tree.md)
* [Reads and writes](usage/reads-and-writes.md)
* [Iteration and range scans](usage/iteration-and-range-scans.md)
* [Maintenance](usage/maintenance.md)
* [WAL modes](durability/wal-modes.md)
* [Production checklist](operations/production-checklist.md)
