# Memory Usage

ZoneTree does not load the entire database into RAM. Its live memory is shaped
by the active write set, pending maintenance, read caches, iterator lifetimes,
and the application's key/value objects.

## Main Consumers

* active mutable-segment B+Tree and Bloom filter,
* frozen in-memory segments waiting for merge,
* sparse disk indexes,
* decompressed disk blocks,
* materialized 16-entry key/value chunks,
* circular per-record key/value caches,
* thread-local sequential search-hint buffers,
* iterator prefetch buffers and segment leases,
* merge, compression, and WAL buffers,
* user key/value objects retained by in-memory segments.

## Mutable Segment

`MutableSegmentMaxItemCount` defaults to `1_000_000`. It is a record limit, not
a byte budget. A million compact structs and a million documents have very
different memory footprints.

```csharp
using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetMutableSegmentMaxItemCount(100_000)
    .OpenOrCreate();
```

The Bloom filter requests `MutableSegmentBloomFilterBitsPerItem` times the
configured mutable-segment capacity, rounds the result up to a power of two,
and caps it at `2^30` bits. The default request is `8` bits per item. The filter
uses a managed `long[]`, so include array overhead and power-of-two rounding in
capacity estimates.

Read-only in-memory segments retain their completed Bloom filters and records
until maintenance merges them. A backlog multiplies both record and filter
memory.

## Disk Read Memory

The decompressed-block cache is usually the largest read-side consumer. A
cached block retains its decompressed bytes and any materialized-entry cache
attached to it.

`MaterializedEntryCacheSize` defaults to 4096 chunk slots per block. Each
published chunk owns arrays for 16 keys and 16 values. The maximum number of
resident chunks is bounded, but actual byte cost depends strongly on `TKey`,
`TValue`, block contents, collisions, and how many positions have been read.

Circular key and value caches are bounded per disk segment. Search-hint buffers
are allocated lazily per disk segment and calling thread, so many threads
touching many segments can create a larger footprint than the prefetch number
alone suggests.

See [read-path caching](../tuning/read-path-caching.md) for ownership and disable options.

## Iterators

Each low-level disk iterator can allocate prefetch arrays according to
`IteratorOptions.DiskSegmentPrefetchSize`. A ZoneTree iterator may merge several
segment iterators, so total buffer memory depends on the number of active
segments as well as the configured prefetch size.

Iterators also lease the segments they scan. A long-lived iterator can keep a
retired segment and its caches reachable until disposal.

## Maintainer Cleanup

A maintainer automates merge and inactive-cache cleanup work according to
configurable policies. For custom control, `zoneTree.Maintenance` exposes
maintenance state, events, and operations.

```csharp
using var maintainer = zoneTree.CreateMaintainer();

maintainer.BlockCacheLifeTime = TimeSpan.FromMinutes(1);
maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromSeconds(30);
```

Shorter block lifetime lowers retained read memory but can increase I/O and
decompression. It does not override a block currently pinned by a read.

## .NET Process Memory

The .NET runtime may keep previously used memory reserved for reuse. Process
working set and peak memory therefore do not directly equal live ZoneTree
objects. Use managed heap diagnostics, allocation profiles, and retained-object
analysis together with process counters.

## Capacity Checklist

* Estimate bytes per mutable record, not only record count.
* Include every simultaneously frozen segment during a maintenance backlog.
* Include Bloom-filter power-of-two rounding.
* Multiply disk caches by the number of active disk segments and hot blocks.
* Multiply search hints by active threads and touched disk segments.
* Multiply iterator buffers by active iterators and their segment fan-in.
* Measure after warmup and after inactive-cache cleanup.
