# Opening A Tree

ZoneTree instances are created with `ZoneTreeFactory<TKey, TValue>`. The factory collects configuration and opens or creates the underlying storage.

## Open Or Create

```csharp
using ZoneTree;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .OpenOrCreate();
```

Use `OpenOrCreate` for normal application startup when the database may or may not already exist.

## Create

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .Create();
```

Use `Create` when a database must not already exist.

## Open Existing

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .Open();
```

Use `Open` when an existing database is required.

## Transactional Tree

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/tx-app")
    .OpenOrCreateTransactional();
```

Transactional trees support coordinated multi-key operations. Use a non-transactional tree for the fastest simple read/write workloads.

## Configure Before Opening

Set serializers, comparers, WAL options, deletion delegates, and storage options before opening the tree. These choices define the storage format and behavior.

```csharp
using ZoneTree;
using ZoneTree.Comparers;
using ZoneTree.Serializers;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .SetComparer(new Int32ComparerAscending())
    .SetKeySerializer(new Int32Serializer())
    .SetValueSerializer(new Utf8StringSerializer())
    .SetMutableSegmentMaxItemCount(100_000)
    .OpenOrCreate();
```

ZoneTree fills serializers, comparers, and hashers for supported known types.
When Bloom filtering is enabled with a custom comparer, ensure comparer-equal
keys produce the same hash.

## Dispose

Always dispose ZoneTree instances when they are no longer needed.

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .OpenOrCreate();
```

For long-running services, keep the tree open for the service lifetime and dispose during shutdown.
