# Serializers And Comparers

The key components configured when a ZoneTree is created have different
responsibilities:

* the comparer defines key equality and order,
* the key hasher supports Bloom-filter membership checks,
* serializers define the bytes stored in WALs and disk segments.

Comparer ordering and serializer output are persisted storage contracts. The
hasher is a runtime correctness contract for Bloom filtering: it does not
define the persisted key order or binary format.

## Defaults For Known Types

`ZoneTreeFactory<TKey, TValue>` fills missing components for supported common
types. Built-in serializers cover `byte`, `bool`, `char`, `DateTime`,
`decimal`, `double`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`,
`Guid`, `string`, and `Memory<byte>`. Built-in ascending comparers and
compatible hashers cover the same list except `bool`.

Use `Memory<byte>` for byte-sequence keys and values. ZoneTree rejects
`byte[]` because `Memory<byte>` supports efficient slicing without allocating
and copying smaller arrays.

## Comparers Define The Keyspace

An `IRefComparer<TKey>` determines both equality and order.

```csharp
using ZoneTree.Comparers;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetComparer(new Int32ComparerAscending())
    .OpenOrCreate();
```

## Hashers Must Match Equality

`IKeyHasher<TKey>` computes a hash by reference:

```csharp
public interface IKeyHasher<TKey>
{
    int GetHashCode(in TKey key);
}
```

A key hasher is required when the mutable-segment Bloom filter is enabled.
Setting `MutableSegmentBloomFilterBitsPerItem` to `0` disables the filter and
removes this requirement.

The required invariant is:

> If the configured comparer considers two keys equal, the configured hasher
> must produce the same hash code for both keys.

Unequal keys may collide; that only increases Bloom-filter false positives.
Equal keys producing different hashes can cause a false negative and make an
existing key appear absent.

Configure a custom hasher with the comparer:

```csharp
using ZoneTree.Comparers;
using ZoneTree.Hashers;

using var zoneTree = new ZoneTreeFactory<string, long>()
    .SetComparer(new StringOrdinalIgnoreCaseComparerAscending())
    .SetKeyHasher(new OrdinalIgnoreCaseHasher())
    .OpenOrCreate();

sealed class OrdinalIgnoreCaseHasher : IKeyHasher<string>
{
    public int GetHashCode(in string key) =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(key);
}
```

ZoneTree validates a common string case-sensitivity mismatch by comparing
`"a"` and `"A"`. It cannot prove arbitrary custom implementations compatible;
test the invariant over representative and edge-case keys.

See [mutable-segment Bloom filters](bloom-filters.md) for filter sizing,
false-positive behavior, and tuning guidance.

## Serializers Define Persisted Bytes

Serializers affect WAL size, disk size, backup batches, CPU cost, compression,
recovery, and compatibility across application versions.

```csharp
using ZoneTree.Serializers;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetKeySerializer(new Int32Serializer())
    .SetValueSerializer(new Utf8StringSerializer())
    .OpenOrCreate();
```

Custom serializers should have a stable, versioned binary contract. Serializer
output size directly affects memory, merge work, I/O, and compression ratio.

## Metadata And Compatibility

ZoneTree metadata records:

* key and value types,
* comparer type,
* key-hasher type, when configured,
* key- and value-serializer types,
* mutable-segment Bloom-filter density.

When opening an existing database, the loader validates the key type, value
type, comparer type, key-serializer type, and value-serializer type.

Type checks cannot detect a behavioral change inside the same .NET type.
Changing comparer ordering or serializer bytes is a storage migration: create
a new ZoneTree and copy or rebuild the data. A hasher can be changed without
rewriting persisted data. When the mutable-segment Bloom filter is enabled,
the replacement must still satisfy the comparer equality invariant.
