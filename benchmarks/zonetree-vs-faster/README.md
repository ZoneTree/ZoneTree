# ZoneTree vs FASTER

Focused console benchmark for hot `Int64 -> Int64` inserts and successful
random point lookups at single and parallel worker counts. Insert IDs are
deterministic, unique, and randomly distributed across the `Int64` keyspace.

```powershell
dotnet run -c Release -- --records 1M --reads 1M --parallelism 1,16
```

The benchmark creates a fresh store for every engine, parallelism level, and
iteration. Workers begin each measured phase together, and every FASTER worker
owns its own client session. Engine order alternates between iterations to
reduce run-order bias. Lookup values and checksums are validated.

ZoneTree uses its default mutable-segment capacity, default AsyncPrivate WAL,
and a maintainer that performs live background compaction. FASTER uses a
file-backed HybridLog and takes a fold-over HybridLog checkpoint after each
insert workload.

Raw insert throughput is measured independently from write completion. The next
phase measures ZoneTree eviction/compaction or the FASTER checkpoint as elapsed
time. Lookups begin only after that phase, so they do not compete with work left
behind by inserts.

Useful options:

```text
--iterations 3
--warmup 20K
--engine zonetree,faster
--faster-index-size 1M
--faster-memory-bits 30
```

Run `dotnet run -c Release -- --help` for the complete option list.
