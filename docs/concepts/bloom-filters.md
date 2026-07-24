# Bloom Filters

ZoneTree uses Bloom filters to avoid unnecessary lookups in mutable segments.
When a key is definitely absent, ZoneTree skips that segment's B+Tree lookup.
When a key might be present, the ordinary comparer-based lookup continues.

This optimization is especially useful for point reads that must pass through
one or more in-memory segments before reaching persistent data. Skipping those
lookups reduces both B+Tree work and lock contention. When the comparer and
hasher contract described below is satisfied, Bloom filters do not change
lookup results, key ordering, or persisted key/value data.

## Configuration

`MutableSegmentBloomFilterBitsPerItem` controls the requested filter density:

```csharp
using var zoneTree = new ZoneTreeFactory<long, string>()
    .SetMutableSegmentBloomFilterBitsPerItem(8)
    .OpenOrCreate();
```

The default is `8`. Valid values are `0` through `64`; `0` disables the
filter. Higher values generally reduce false positives at the cost of a larger
allocation. They do not eliminate the ordinary lookup after a possible match.
An enabled filter requires a key hasher. The factory supplies one automatically
for supported key types. For other key types, configure a compatible hasher or
set the value to `0` to disable the filter.

Filter size also depends on `MutableSegmentMaxItemCount`. ZoneTree multiplies
that configured capacity by the requested bits per item, uses at least 64
bits, and rounds the result up to a power of two. The allocation is capped at
`2^30` bits, or 128 MiB of bit storage per filter.

With the default mutable-segment capacity of 1,000,000 records and the default
8 bits per item, the requested 8,000,000 bits round up to 8,388,608 bits: 1 MiB
of bit storage.

Power-of-two rounding creates allocation steps. A small increase in requested
bits per item can therefore double the allocated filter size when it crosses
the next power-of-two boundary.

## Membership Semantics

A Bloom-filter result has two forms:

* **definitely absent:** ZoneTree skips the mutable segment's B+Tree lookup;
* **possibly present:** ZoneTree performs the normal B+Tree lookup.

A false positive only adds an unnecessary lookup. A false negative can hide an
existing key and is therefore not acceptable.

ZoneTree adds keys to the filter before publishing their B+Tree writes. Reads
and writes can use the filter concurrently. Each key uses three bit probes in
one 64-bit word, allowing a write to publish all three bits with one atomic
operation and a read to inspect them with one volatile read.

Updates and deletion markers also add their key hashes. Adding the same hash
again leaves the filter unchanged.

## Hasher Correctness

The configured `IKeyHasher<TKey>` must agree with the configured
`IRefComparer<TKey>`:

> If the comparer considers two keys equal, the hasher must return the same
> hash code for both keys.

Unequal keys may share a hash; that only increases false positives. Equal keys
with different hashes can produce false negatives.

`ZoneTreeFactory<TKey, TValue>` supplies compatible hashers for supported
known key types. A custom comparer normally requires a custom hasher:

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

Option validation detects the common case where a string comparer treats
`"a"` and `"A"` as equal but the hasher does not. It cannot prove arbitrary
custom comparer and hasher implementations compatible. Test this contract over
representative keys and equality edge cases.

See [key components](serializers-and-comparers.md) for the complete comparer,
hasher, and serializer contracts.

## False Positives

ZoneTree uses three probes per key. Assuming uniform hashes, no allocation
rounding, and a filter filled to its configured item capacity, the conventional
approximation is:

```text
false-positive rate ~= (1 - exp(-3 / bitsPerItem))^3
```

Approximate rates before power-of-two rounding are:

| Requested bits per item | Approximate false-positive rate |
| ---: | ---: |
| `4` | `14.7%` |
| `8` | `3.1%` |
| `16` | `0.5%` |
| `32` | `0.07%` |

These are planning estimates, not guarantees. Actual results depend on hash
quality, distinct-key count, filter occupancy, power-of-two rounding, and the
real key distribution. Rounding often gives the filter more bits than
requested; the maximum-size cap can give a very large segment fewer effective
bits per item.

## Lifetime And Scope

Each mutable segment owns its filter. The filter remains available after that
segment freezes and waits to be merged. A maintenance backlog can therefore
retain several filters at once.

The bit array is not persisted as a disk structure and does not filter disk
segments. After reopening a tree, read-only segments reconstructed from their
WALs do not currently receive Bloom filters. The configured hasher type and
bits-per-item value are recorded in ZoneTree metadata for inspection.

Bloom filters accelerate point membership checks such as `TryGet` and
`ContainsKey`. Iterators and range scans do not use them to navigate ordered
data.

## Tuning

Keep the default `8` bits per item unless measurements show a reason to change
it. Evaluate the complete workload because the filter trades write and memory
cost for cheaper negative reads:

* reads hash the key when a filter is checked;
* writes hash each inserted, updated, or deleted key and perform an atomic bit
  publication;
* every active or frozen mutable segment retains its own bit array;
* false positives still enter the normal B+Tree lookup path.

Lower values or `0` can help workloads dominated by writes or mutable-segment
hits. Higher values can help point-read workloads where keys are commonly
absent from mutable segments and avoiding B+Tree contention matters. Compare
single-thread and parallel throughput, write cost, lookup latency, retained
memory, and observed false-positive behavior before changing the default.

See [memory usage](../storage/memory-usage.md) for whole-tree capacity planning and
[read-path caching](../tuning/read-path-caching.md) for the other read accelerators.
