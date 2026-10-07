# Performance Benchmark

Benchmarks are useful only when the workload is concrete enough to explain. ZoneTree's profile-store benchmark compares ZoneTree, RocksDB, SQLite, and MySQL on a live application-style workload: profile inserts, secondary-index maintenance, point reads, email lookups, ordered index scans, indexed profile queries, updates, reopen verification, storage size, and process memory.

This benchmark is not a synthetic insert-only loop. It models a service-side profile store where every profile is written as an individual live operation and then queried through primary and secondary access paths.

## Reference Results

Reference reports include the exact machine, configuration, phase timings, checksums, storage size, memory measurements, and generated charts for each profile count.

| Profiles | Report |
| ---: | --- |
| 100K | [100K reference](reference/100k/100k.md) |
| 500K | [500K reference](reference/500k/500k.md) |
| 1M | [1M reference](reference/1m/1m.md) |
| 2M | [2M reference](reference/2m/2m.md) |
| 5M | [5M reference](reference/5m/5m.md) |
| 10M | [10M reference](reference/10m/10m.md) |
| 20M | [20M reference](reference/20m/20m.md) |
| 50M | [50M reference](reference/50m/50m.md) |
| 100M | [100M reference](reference/100m/100m.md) |

The reference reports should be treated as evidence for this exact workload, hardware, operating system, .NET runtime, engine configuration, and durability profile. Results can change with CPU, storage, memory pressure, compression settings, WAL settings, segment sizing, and query distribution.

## Workload

Each engine stores the same profile model:

```csharp
UserProfile(
    UserId,
    Email,
    Country,
    Status,
    CreatedAtUnixMs,
    LastLoginUnixMs,
    Reputation,
    DisplayName,
    Bio)
```

The benchmark measures:

* individual profile inserts,
* primary-key reads by user id,
* secondary lookup by email,
* index-only scans over country/status, created-at, and reputation orderings,
* indexed queries that scan an index and fetch matching profiles,
* individual profile updates,
* post-update reads, lookups, scans, and queries,
* final settle/checkpoint, reopen, verification, storage size, and process peak memory.

Every phase contributes to deterministic checksums. Incorrect implementations cannot win by skipping reads, omitting index maintenance, or returning different result sets.

## Engine Layout

ZoneTree stores the primary profile data and secondary indexes in separate ZoneTree instances. This mirrors how applications commonly build indexing layers on top of ZoneTree.

RocksDB stores the primary profile data and secondary indexes in separate RocksDB databases. The benchmark intentionally uses separate writes rather than a `WriteBatch` so the RocksDB layout mirrors the ZoneTree tree layout.

SQLite and MySQL store the profile table with native SQL indexes. They maintain secondary indexes inside the database engine.

MySQL is a client/server baseline measured over TCP. Its process memory in the reports is the benchmark client's .NET process memory, not the MySQL server or container memory.

## Durability Profile

The benchmark uses practical high-throughput durable settings:

| Engine | Durability profile |
| --- | --- |
| ZoneTree | Async compressed WAL, compressed disk segments, application-managed secondary indexes |
| RocksDB | WAL enabled, Zstd compression, five separate databases |
| SQLite | WAL journal mode, `synchronous=NORMAL`, native SQL indexes |
| MySQL | InnoDB, benchmark Docker disables binlog, `innodb_flush_log_at_trx_commit=2`, `sync_binlog=0` |

These settings are meant to compare practical service configurations, not identical internal durability mechanisms.

## How To Read The Reports

`Completed phase time` is the sum of measured workload phases. It excludes initialization, stabilization, final settle/checkpoint, reopen, verification, and report writing.

`Run time` is the full engine run duration, including overhead outside measured phases.

`Pre-read stabilize` and `Post-update stabilize` are used for ZoneTree and RocksDB before read/query measurements. This reduces background maintenance and compaction noise in lookup and query phases.

`Storage` is measured after the engine settles or checkpoints its data.

`Process peak memory` is measured from the benchmark process. For embedded engines, this includes the engine inside the benchmark process. For MySQL, it does not include the server process.

Peak memory is a high-water mark, not steady-state memory usage. During reads and scans, ZoneTree can cache decompressed disk blocks in memory; blocks that are not accessed within the configured cache lifetime are released by maintenance. After release, the .NET runtime may keep the reserved memory region available for reuse instead of immediately returning it to the operating system.

`Index scan throughput` measures index-only scans. `Query throughput` measures scans that also fetch profile records.

## Current Takeaways

In the available reference reports, ZoneTree shows strong performance for this live indexed profile-store workload:

* high single-operation insert throughput,
* high update throughput while maintaining secondary indexes,
* fast primary-key reads and email lookups,
* fast ordered index scans and profile-fetching indexed queries,
* compact storage compared with RocksDB and SQLite on the same generated data.

The main tradeoff visible in the reports is memory. ZoneTree uses more benchmark-process memory in exchange for very high read, write, and query throughput. This is intentional and configurable; for example, disk block cache lifetime controls how long hot blocks stay in memory. Also, .NET process peak memory is a high-water mark, so it does not mean ZoneTree actively uses all of that memory for the whole run.

## Reproduce The Benchmark

The benchmark source lives under `benchmarks/profile-store`.

From `benchmarks/profile-store`, a full local reference run looks like:

```bash
dotnet run --project src\ProfileStore.Benchmark.csproj -c Release -- --mysql-host 192.168.178.25 --mysql-port 3306 --mysql-user root --mysql-password "DevMySql_123456!" --mysql-database profilebench --output results --data data --update-latest --timeout-seconds 1200 --engine all --profiles 100K,500K,1M,2M,5M,10M
```

The Docker setup provides a benchmark-oriented MySQL container and runs the benchmark in Release mode. See the benchmark README for exact Docker commands and cleanup instructions.

## Use Results Carefully

Do not generalize these numbers to every database workload. This benchmark is most relevant when the application needs:

* live individual writes instead of bulk imports,
* ordered keys and range scans,
* secondary indexes maintained by the application or database engine,
* profile/document-style values,
* predictable reopen and verification behavior,
* local or service-side storage where embedding a storage engine is practical.

For different record sizes, key distributions, write batching, transaction boundaries, durability requirements, compression choices, or server deployment models, run the benchmark again with the target workload and hardware.
