# Profile Store Benchmark

This project compares ZoneTree, RocksDB, SQLite, and MySQL using the same
application-level profile store. It exercises primary-key access,
application-managed or native secondary indexes, ordered scans, profile-fetching
queries, updates, persistence, reopen, and verification.

This is not a universal database ranking. It is a reproducible workload for a
local or service-side profile store with individual writes, multiple secondary
indexes, and mixed read patterns.

## Benchmark Policy

### Managed code compilation

Reference performance runs should disable .NET tiered compilation:

```powershell
$env:DOTNET_TieredCompilation = "0"
```

```cmd
set DOTNET_TieredCompilation=0
```

```bash
export DOTNET_TieredCompilation=0
```

This setting does **not** bypass JIT compilation and does not precompile or warm
up the benchmark. It disables the tiered JIT pipeline. With the default runtime
behavior, methods can begin as quickly generated Tier 0 code and later be
recompiled as optimized Tier 1 code. With tiered compilation disabled, methods
that require JIT compilation are optimized on their first compilation.

The distinction matters here because ZoneTree is managed code, while most of
RocksDB's work executes in an already-compiled native library. This benchmark
starts a fresh process for each engine and does not perform an unmeasured warmup
for every phase. Tier 0 execution and background tier transitions can therefore
affect ZoneTree much more than RocksDB.

Disabling tiered compilation makes the reference runs steady-state-oriented and
reduces tier-up variability. It also changes the runtime behavior being
measured. Do not compare results produced with different tiered-compilation
settings. The benchmark still includes first-use JIT compilation that occurs
inside a measured phase.

See the [.NET compilation configuration documentation](https://learn.microsoft.com/dotnet/core/runtime-config/compilation)
for the runtime's tiered compilation, quick JIT, and dynamic PGO behavior.

Server GC is enabled by the benchmark project for every engine.

## Quick Start

Run the embedded engines from this directory:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- \
  --engine zonetree,rocksdb,sqlite \
  --profiles 100K \
  --parallelism 1,16 \
  --clean
```

`--profiles` accepts one value or a comma-separated list. `K` and `M` suffixes
are supported. `--parallelism` also accepts one value or a comma-separated
list.

For a Linux host without Docker, see
[bare-metal-server-setup.md](bare-metal-server-setup.md).

### Run all engines with Docker

Build the benchmark image and start the configured MySQL service:

```bash
docker compose build benchmark
docker compose up -d mysql
```

Run the benchmark with tiered compilation disabled inside the container:

```bash
docker compose run --rm \
  -e DOTNET_TieredCompilation=0 \
  benchmark \
  --engine all \
  --profiles 100K,1M \
  --parallelism 1,16 \
  --output /workspace/benchmarks/profile-store/results \
  --data /workspace/benchmarks/profile-store/data
```

The engine stores are reset before measurement. To remove old Docker reports as
well, clear the host `results/` directory before starting the container; do not
ask the benchmark to delete the bind-mount root itself.

The bundled service uses `mysql:9.7.1` with binary logging disabled,
`innodb_flush_log_at_trx_commit=2`, `sync_binlog=0`, an 8 GB buffer pool, and
4 GB redo capacity. These are benchmark settings, not production guidance.

For a separately managed MySQL server, configure `MYSQL_HOST`, `MYSQL_PORT`,
`MYSQL_DATABASE`, `MYSQL_USER`, and `MYSQL_PASSWORD`, or pass the corresponding
`--mysql-*` arguments. See
[mysql-benchmark-tuning.md](mysql-benchmark-tuning.md) for a ready-to-run server
configuration.

## Workload

Multi-engine runs launch each engine in a separate child process and aggregate
the results afterward. Every engine starts from an empty store. Phases remain
sequential; only the operations inside a phase are concurrent.

### Default size

| Setting | Default |
| --- | ---: |
| Profiles | 100,000 |
| Parallelism | 1 |
| Primary-key reads | profiles |
| Email lookups | profiles |
| Profile updates | profiles |
| Post-update primary-key reads | profiles |
| Post-update email lookups | profiles |
| Query count, up to 10,000 profiles | profiles |
| Query count, above 10,000 profiles | profiles / 4 |
| Post-update query count | same rule as query count |
| Results returned per query | 50 |

The query limit is intentionally capped at 50 so profile-fetching queries do
not dominate total runtime. Use `--query-limit`, `--query-count`, and
`--post-query-count` for query-focused or shorter runs.

### Measured phases

1. Insert profiles.
2. Stabilize engines that require read stabilization.
3. Read profiles by user id.
4. Look up profiles by email.
5. Scan and query the country/status index.
6. Scan and query created-at ranges.
7. Scan and query the top-reputation index.
8. Update profiles.
9. Stabilize again when required.
10. Repeat primary-key, email, country/status, and top-reputation reads.
11. Settle or checkpoint, close, reopen, and verify persisted data.

The insert and update timings contain only the write phases. Stabilization is
reported separately and is also combined with write time in the stabilized
write-throughput chart.

### Parallel execution

With `--parallelism N`, each measured phase uses `N` workers. All workers reach
a common start gate before the phase stopwatch begins, then they are released
together. Worker creation and initial scheduling are outside the measured
interval.

SQLite and MySQL use one connection and one prepared-command set per worker.
ZoneTree and RocksDB workers share their engine's opened storage handles.
Repeated updates for the same user id are assigned to the same worker so their
original order is preserved.

Each phase calculates a checksum. Multi-engine runs compare checksums between
engines using the same profile count, seed, and parallelism. Final verification
also checks the reopened store. Checksums from different parallelism levels are
not expected to be identical because phase results are partitioned by worker.

## Data Model

Each engine stores this logical record:

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

The workload maintains these indexes:

* email -> user id
* country + status + user id
* created-at + user id
* reputation descending + user id

SQLite and MySQL use native SQL indexes. RocksDB uses separate databases for
profile and index records. ZoneTree uses separate application-managed trees.

## Engine Configuration

The report records the effective engine settings. Important defaults are:

| Engine | Configuration |
| --- | --- |
| ZoneTree | AsyncPrivate WAL, live background maintainers, 250,000 mutable-segment items, sparse step 16, key cache 1,024, value cache 8,192, iterator prefetch 16 |
| RocksDB | WAL enabled, Zstd compression, five database instances, 1,024 MB write buffer per database, four write buffers, unsynchronized writes |
| SQLite | WAL, `synchronous=NORMAL`, 1,024 MB page cache, 1,024 MB mmap allowance, memory temp storage, autocommit writes |
| MySQL | InnoDB, native indexes, autocommit writes; bundled Docker durability settings described above |

ZoneTree and RocksDB are stabilized after insert and update phases before read
and query measurements. ZoneTree settles active data and waits for maintenance;
RocksDB compacts its databases. SQLite and MySQL do not perform an equivalent
explicit stabilization phase.

Durability modes are practical comparison points, not identical guarantees.
MySQL is a server baseline rather than an embedded peer. Its server process and
container memory are not included in the benchmark client's peak working-set
measurement.

Results remain sensitive to CPU topology, storage, available memory, operating
system, runtime configuration, Docker overhead, and background activity. Use
clean runs on the same documented machine for performance comparisons.

## Important Options

Run `dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --help`
for the complete option list.

| Option | Purpose |
| --- | --- |
| `--engine` | One engine, `all`, or a comma-separated engine list |
| `--profiles` | One profile count or a comma-separated list |
| `--parallelism` | One worker count or a comma-separated list |
| `--query-limit` | Maximum results returned by each index query |
| `--read-count`, `--email-read-count` | Override pre-update lookup counts |
| `--query-count`, `--post-query-count` | Override query operation counts |
| `--update-count` | Override update operations |
| `--post-read-count`, `--post-email-read-count` | Override post-update lookup counts |
| `--seed` | Select the deterministic workload |
| `--timeout-seconds` | Limit each engine child process |
| `--data`, `--output` | Select data and result roots |
| `--clean` | Delete both configured roots before the run |
| `--clean-data`, `--clean-results` | Delete only one configured root |
| `--update-latest` | Publish generated reports under `reference/<os>/` |

Engine-specific cache, sparse-array, mutable-segment, prefetch, SQLite, and
RocksDB write-buffer options are listed by `--help` and recorded in JSON and
Markdown reports.

## Smoke Runs and Timeouts

Use smaller operation counts while changing benchmark code:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- \
  --engine zonetree,rocksdb,sqlite \
  --profiles 10K \
  --parallelism 1,16 \
  --read-count 2000 \
  --email-read-count 2000 \
  --query-count 100 \
  --update-count 1000 \
  --post-read-count 2000 \
  --post-email-read-count 2000 \
  --post-query-count 100 \
  --clean
```

`--timeout-seconds` limits each engine run. A timeout produces a partial result
containing completed phases, marks the interrupted phase, and continues with
the next requested engine.

## Output

P1 results preserve the original directory name. Higher parallelism levels add
a suffix:

```text
results/profiles-1000000/
results/profiles-1000000-p16/
```

Each directory contains:

```text
profile-store-YYYYMMDD-HHMMSS.json
profile-store-YYYYMMDD-HHMMSS.md
profile-store-YYYYMMDD-HHMMSS-execution-time.svg
profile-store-YYYYMMDD-HHMMSS-write-throughput.svg
profile-store-YYYYMMDD-HHMMSS-lookup-throughput.svg
profile-store-YYYYMMDD-HHMMSS-index-scan-throughput.svg
profile-store-YYYYMMDD-HHMMSS-query-throughput.svg
profile-store-YYYYMMDD-HHMMSS-resources.svg
```

Multi-engine child results are retained below `.runs/`. The Markdown report
contains workload and engine settings, environment details, phase timings,
throughput, stabilization time, settle time, reopen verification, storage size,
process peak memory, checksums, and charts.

Generated files under `results/` are local artifacts. Commit only curated
reference results under:

```text
reference/<os>/profiles-<count>/
reference/<os>/profiles-<count>-p<parallelism>/
```

### Regenerate reports

Regenerate charts for one JSON report:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- \
  --render-charts results/profiles-10000/profile-store-YYYYMMDD-HHMMSS.json
```

Regenerate only its Markdown report:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- \
  --render-markdown results/profiles-10000/profile-store-YYYYMMDD-HHMMSS.json
```

Regenerate every committed reference report from existing `latest.json` files:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- \
  --render-reference-reports
```

An optional reference directory can follow `--render-reference-reports`.

### Publish reference results

Configure MySQL through environment variables, disable tiered compilation, and
run from a quiet machine:

```bash
export DOTNET_TieredCompilation=0
export MYSQL_HOST=127.0.0.1
export MYSQL_PORT=3306
export MYSQL_DATABASE=profilebench
export MYSQL_USER=root
export MYSQL_PASSWORD=your-password

dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- \
  --engine all \
  --profiles 100K,500K,1M,2M \
  --parallelism 1,16 \
  --timeout-seconds 120000 \
  --output results \
  --data data \
  --update-latest
```

`--update-latest` writes `latest.json`, `latest.md`, and matching SVG files to
the current platform directory, such as `reference/win/` or `reference/linux/`.

## Optional GC Memory Tuning

The project uses Server GC. Additional runtime controls can change both memory
usage and throughput and must remain consistent across compared runs:

```bash
export DOTNET_GCConserveMemory=1
export DOTNET_GCHighMemPercent=0x50
```

`DOTNET_GCConserveMemory` ranges from `0` to `9`; higher values ask the GC to
work harder to reduce managed-heap size. `DOTNET_GCHighMemPercent` controls the
physical-memory percentage at which the GC treats the machine as under high
memory load. Environment variable values are hexadecimal, so `0x50` means 80%.

These are runtime-wide controls. Prefer ZoneTree's storage, block cache,
sparse-array, and cache-size options when tuning ZoneTree itself.
