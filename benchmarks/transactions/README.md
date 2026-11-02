# Transaction benchmark

Compares ZoneTree, RocksDB and SQLite transactions across many use cases instead of one big workload. Each scenario answers one application question, so you can read the ones that match your workload and ignore the rest.

## Commands to copy

### 1. Set up the terminal

Do this once per terminal. It turns off the JIT's tiering, which reference runs require.

PowerShell:

```powershell
cd benchmarks/transactions/src
$env:DOTNET_TieredCompilation = 0
```

Windows Command Prompt:

```bat
cd benchmarks\transactions\src
set DOTNET_TieredCompilation=0
```

bash:

```bash
cd benchmarks/transactions/src
export DOTNET_TieredCompilation=0
```

The `dotnet` commands below work the same in all three. Copy the `dotnet` line, because Command Prompt does not understand the `#` comments.

### 2. Check that everything runs

Every scenario on every engine, with small jobs. The numbers mean nothing; it only proves the benchmark works.

```bash
dotnet run -c Release -- --preset smoke
```

### 3. Run one category

Each line runs one category at 1M keys on every engine, at 1, 4 and 16 workers. Pick the question you care about.

```bash
# How fast are the smallest transactions when contention is low?
dotnet run -c Release -- --keys 1M --scenario category:single-key

# What happens as more workers fight for fewer keys?
dotnet run -c Release -- --keys 1M --scenario category:contention

# What do realistic multi-key units of work and batches cost?
dotnet run -c Release -- --keys 1M --scenario category:multi-key

# What do read-heavy workloads cost, and what does validating reads cost?
dotnet run -c Release -- --keys 1M --scenario category:mixed

# How fast are consistent reads, and how much do writers disturb them?
dotnet run -c Release -- --keys 1M --scenario category:snapshot-reads

# Which anomalies does each engine allow, and what does preventing them cost?
dotnet run -c Release -- --keys 1M --scenario category:isolation

# What do abandoned transactions cost?
dotnet run -c Release -- --keys 1M --scenario category:failure

# What does one huge transaction cost in time and memory?
dotnet run -c Release -- --keys 1M --scenario category:large

# What does waiting for the disk cost, and do concurrent commits share syncs?
dotnet run -c Release -- --keys 1M --scenario category:durability
```

### 4. Narrow it down or change it

Add any of these to a command above:

```bash
--scenario rmw-uniform,hot-1                  # these scenarios, not a whole category
--scenario category:single-key,category:mixed # several categories
--engine zonetree                             # all 4 ZoneTree variants
--engine rocksdb                              # both RocksDB variants
--engine sqlite                               # SQLite
--engine zonetree-mvcc-ser-async,sqlite       # these variants
--workers 1,2,4,8,16                          # these worker counts
--keys 100K                                   # fewer keys mean smaller jobs and a shorter run
--scale 0.1                                   # a tenth of each job
```

`--list-scenarios` prints every category and scenario.

### 5. Keep the results in the reference report

Without `--update-latest`, results go to `out/results/<stamp>/<keys>/summary.md`. With it, they also merge into `reference/<os>/standard/<keys>/summary.md`, and each run replaces only the earlier result of the same scenario, engine, worker count and key count.

```bash
# Refresh ZoneTree in one category after a code change.
dotnet run -c Release -- --keys 1M --scenario category:single-key --engine zonetree --workers 1,2,4,8,16 --update-latest

# Fill a gap: an engine that has not run at a worker count.
dotnet run -c Release -- --keys 1M --scenario rmw-uniform --engine sqlite --workers 8 --update-latest

# Rewrite the reports after changing the report code, without running anything.
dotnet run -c Release -- --render-latest
```

### How many runs a command starts

Runs = scenarios × worker counts × engines, except that `large-txn` always uses 1 worker. The benchmark prints the total when it starts and counts up as `[1/564]`. The same scenario appears many times in that log, once for each engine and worker count.

| Category | Scenarios | Runs: `--engine zonetree --workers 1,2,4,8,16` | Runs: all engines, `--workers 1,2,4,8,16` |
| --- | ---: | ---: | ---: |
| `single-key` | 3 | 60 | 105 |
| `contention` | 3 | 60 | 105 |
| `multi-key` | 5 | 100 | 175 |
| `mixed` | 2 | 40 | 70 |
| `snapshot-reads` | 5 | 100 | 175 |
| `isolation` | 4 | 80 | 140 |
| `failure` | 2 | 40 | 70 |
| `large` | 1 | 4 | 7 |
| `durability` | 4 | 80 | 140 |
| **All** | **29** | **564** | **987** |

### Good to know

- **Stopping and resuming:** Ctrl+C is safe. You lose only the run in progress, because reports update after every run. To continue, run the same command with `--resume` at the end. It skips the finished runs, runs what is left and any failed or timed out runs, and keeps writing to the same report. It continues the newest execution in `out/results/` that finished a run, and refuses if that execution used a different command or environment. It cannot detect code you changed without changing the ZoneTree version, so do not resume across a code change you want to measure.
- **One at a time:** do not start a second benchmark or rebuild while one is running. Runs start from the build output, and a second benchmark would disturb the measurements.
- **Disk space:** a stopped run can leave its database in `out/data/`. Add `--clean-data` to the next command to empty that folder first.
- **`--update-latest would mix different settings`:** the reference was measured under other settings, and the message lists them. You have three choices:
  - Use the same settings, including `DOTNET_TieredCompilation`.
  - Add `--force` to mix them. The report then warns that its runs do not compare directly, until the older runs are replaced.
  - Delete that key count's folder to start its reference over: `rm -r ../reference/win/standard/1M` in bash, `Remove-Item -Recurse ../reference/win/standard/1M` in PowerShell, or `rmdir /s /q ..\reference\win\standard\1M` in Command Prompt.

## Results

Each key count has its own report, and database files go to `out/data/`. Open the report's `summary.md` first: it has one comparison table and chart per category, and links to a page per scenario.

## Engines

Add `--raw` to run the same scenarios without explicit transactions on **all selected engines**:

```powershell
dotnet run -c Release -- --preset standard --clean --keys 100K --engine all --workers 1,2,4,8,16 --scenario batch-1000 --raw
```

ZoneTree uses ordinary `OpenOrCreate` with direct reads, `Upsert`, deletes and iterators. RocksDB opens a plain DB and calls `Get`, `Put` and `Delete` directly. SQLite runs statements in autocommit mode without explicit `BEGIN`, `COMMIT` or `ROLLBACK`.

Raw mode selects `zonetree-raw-async`, `zonetree-raw-sync`, `rocksdb-raw` and `sqlite-raw`. Serializable/snapshot ZoneTree variants collapse to one raw variant per WAL mode; optimistic/pessimistic RocksDB variants collapse to one plain-DB variant. Engine filters still apply, and the raw IDs can also be selected directly. Defaults without `--raw` remain transactional.

Each scenario keeps its operation counts, keys, values and batching loop. Writes take effect immediately; scenario commit/rollback boundaries do not provide transactions. In raw reports, the existing commit and latency counters describe completed scenario operation groups. Raw runs skip invariant verification and anomaly counting, and reports mark checks as skipped rather than passed or broken. Scenario reads and writes still execute; actual engine errors are still reported as failures.

Durable scenarios retain their durability setting: ZoneTree syncs at the scenario commit boundary, RocksDB uses `sync=true` per direct write, and SQLite uses `synchronous=FULL` with autocommit statements. These native durability costs are included in the measurement.

Raw engine IDs are distinct in result files, data directories, charts and reference reports. `--update-latest` can add them alongside transactional results with the same workload settings.

| Id | Engine | Isolation |
| --- | --- | --- |
| `zonetree-mvcc-ser-async`, `zonetree-mvcc-ser-sync` | ZoneTree MVCC transactional tree, serializable, async and sync WAL | A commit fails if a newer commit wrote a key it wrote or read, or a key inside a range it read. |
| `zonetree-mvcc-snap-async`, `zonetree-mvcc-snap-sync` | ZoneTree MVCC transactional tree, snapshot, async and sync WAL | A commit fails if a newer commit wrote a key it wrote or read for update. Plain reads are not protected. |
| `rocksdb-optimistic` | `OptimisticTransactionDB`, one snapshot per transaction | A commit fails if a newer commit wrote a key it wrote or read for update. Plain reads and ranges are not protected. |
| `rocksdb-pessimistic` | `TransactionDB`, key locks, deadlock detection, no snapshot in write transactions | Writes and reads for update lock the key and read the latest value. Plain reads see the latest commit, so they are not repeatable. |
| `sqlite` | WAL mode, `BEGIN IMMEDIATE` | One write transaction at a time, so writes are serializable and never conflict. Readers keep their snapshot and do not block. |

Read-only work uses each engine's snapshot: `BeginReadOnlyTransaction` on ZoneTree, an explicit snapshot on RocksDB, a deferred transaction on SQLite.

`--engine` accepts `all`, a family (`zonetree`, `rocksdb`, `sqlite`) or variant ids.

The ZoneTree `-async` ids use the `AsyncCompressed` WAL, where a background writer does the WAL work. The `-sync` ids use `SyncCompressed`, where the writing thread appends to a memory-mapped tail file. Both variants disable WAL compression in this benchmark.

RocksDB runs with a 1 GB block cache and bloom filters of 10 bits per key, so reads are not limited by its 8 MB default cache. SQLite's page cache has the same size. `--rocksdb-block-cache-mb` changes it.

### Durability

All three engines can make every commit durable before it returns. The `durability` category measures that, with each engine's strongest setting:

| Engine | Durable scenarios |
| --- | --- |
| ZoneTree | `TryCommitDurableAsync`: the commit, then `SyncWALAsync`. Concurrent commits share disk syncs. |
| RocksDB | `sync=true`, with RocksDB's group commit. |
| SQLite | `synchronous=FULL`. |

The other categories use each engine's usual setting, where a commit does not wait for the disk. Each engine then has its own exposure:

| Engine | Default setting | Process crash can lose | Power loss can lose |
| --- | --- | --- | --- |
| ZoneTree sync | `SyncCompressed` WAL, compression disabled | Nothing. | Writes since the last completed WAL sync. The periodic job syncs the complete dirty WAL, with a `TailWriterJobInterval` delay between runs. Its durable prefix stays protected across later rollovers. |
| ZoneTree async | `AsyncCompressed` WAL, compression disabled | Records still queued for the background writer. Queue size and scheduling determine the exposure. | Queued records and writes since the last completed WAL sync. The writer syncs the complete dirty WAL when its queue empties; sustained load can delay that sync across many main-file blocks. |
| RocksDB | `sync=false` | Nothing. | What the OS has not written back since the last memtable flush. The WAL is not synced per commit or on a timer, and a flush makes its data durable, so at most the commits in the memtables: up to `max_write_buffer_number` buffers of `write_buffer_size`. |
| SQLite | `synchronous=NORMAL` | Nothing. | What the OS has not written back since the last checkpoint. A checkpoint syncs, and runs about every `wal_autocheckpoint` WAL pages, so at most about that much WAL, or more while readers hold a checkpoint back. |

`SyncCompressed`, RocksDB and SQLite lose nothing on a process crash. `AsyncCompressed` queues records for a background writer, so it can lose queued writes on a process crash as well as on a power loss. ZoneTree's automatic syncs protect the complete dirty WAL, but their scheduling does not provide a hard persistence deadline. Use an explicit sync to acknowledge durability across all segments.

ZoneTree's full crash guarantee requires a successful `SyncWALAsync` after committing, as measured by the `durability` category. Without that wait, writes across segments can persist independently: an unsynced transaction may recover partially, and a later commit may survive while an earlier unsynced commit is lost. Operation indexes order surviving operations but cannot restore missing records.

Durable runs wait for the disk on every commit, so their throughput shows the disk more than the engine. The usual pattern is to commit a batch and wait once, which `durable-batch-100` measures.

### Retries

A conflict restarts the unit of work at once, on every engine, the way an application's retry loop does. `--retry-backoff` adds an exponential randomized wait, to measure whether backing off helps under contention.

## Scenarios

| Category | Scenario | The application question |
| --- | --- | --- |
| `single-key` | `rmw-uniform` | Counters and small state updates spread over many keys. |
| | `rmw-zipf` | Real traffic, where a few keys take most of the updates. |
| | `insert-unique` | Event logs and new records with hashed ids: inserts that cannot conflict. |
| `contention` | `hot-1`, `hot-16`, `hot-256` | All workers fight for one key, or for a small hot set. |
| `multi-key` | `transfer` | Payments: two keys change together while an auditor checks the total in snapshots. |
| | `order-entry` | OLTP: 80% orders that update a customer and 3 stock items and insert a row, 20% short range reads. |
| | `batch-10`, `batch-100`, `batch-1000` | Ingestion in batches, per-worker keys, no conflicts. |
| `mixed` | `mixed-95-5` | Read-mostly services with skewed keys. |
| | `read-8-write-2` | Transactions that decide from several keys. Serializable engines validate the reads. |
| `snapshot-reads` | `snapshot-point` | The read path alone, with no writers. |
| | `snapshot-point-under-write` | Consistent point reads with 2 writers running. |
| | `range-100-under-write` | Listing 100 rows while 2 writers change them. |
| | `full-scan-under-write` | Analytics and exports over live data. |
| | `long-reader` | What one old snapshot costs the writers, and whether it stays exact. |
| `isolation` | `write-skew`, `write-skew-guarded` | A rule over keys a transaction reads but does not write, with plain reads or reads for update. |
| | `phantom`, `phantom-guarded` | A rule over a range of keys, with plain ranges or a guard key. |
| `failure` | `rollback-all`, `rollback-10pct` | What abandoned transactions cost. |
| `large` | `large-txn` | One huge transaction: time per phase, memory, and the same size rolled back. |
| `durability` | `durable-rmw`, `durable-batch-100` | What waiting for the disk costs, and whether concurrent commits share syncs. |
| | `durable-transfer`, `durable-order-entry` | The multi-key scenarios when every commit must survive a power loss. |

### Reading the isolation scenarios

`write-skew` and `phantom` are not races to win. Their anomaly counts show what each isolation level allows: a transaction that commits after seeing a broken rule. Expect zero from `zonetree-mvcc-ser-async`, `zonetree-mvcc-ser-sync` and `sqlite`, and non-zero from the others. A non-zero count there is the documented behavior of that level, so it does not count as a failure.

The `-guarded` variants add what an application on a weaker level would add, so every engine must report zero. There, an anomaly means the run failed.

## What a run does

Every run is one engine, one scenario, one worker count and one key count, in its own process with a new database:

1. Load the keys in transactions of 1000, then settle.
2. Do the job, and measure the time from its start until the last worker finishes.
3. Close the database cleanly, without settling, and reopen it.
4. Check the scenario's invariant on the reopened database, settle, close, and measure the storage.

### The job

Every scenario has a job: a fixed number of transactions, set per scenario in `ScenarioCatalog` for 100K keys. It grows with `--keys` and `--scale`, so 1K keys do one hundredth of the work of 100K keys. The workers split it evenly, so more workers do the same job in less time. There is no warmup and no time limit other than `--timeout-seconds`; the whole job counts.

Each worker's key sequence comes from a fixed seed and does not depend on retries, so every engine does the same work and the storage and reopen numbers compare the same data. The exceptions are the outcomes that depend on the engine: `order-entry` reads each customer's latest count, and the `isolation` scenarios decide their writes from what they read.

Auditors and other background roles are not part of the job. They run until it is done, so a faster engine runs fewer audits; compare their rates, not their totals.

A full scan's job is 100 scans. Each scan already reads every key, so it grows with `--keys` on its own.

A failed or timed out run becomes a row in the report, and the benchmark continues. The exit code is non-zero if any run failed or broke an invariant.

## What the report shows

| Metric | Meaning |
| --- | --- |
| Job, Time | The transactions the run had to do, and how long they took. |
| Ops/s | The job divided by its time. |
| Rows/s | Rows read or written per second. The comparison uses it for scans and batches. |
| Conflicts | Commit attempts that conflicted and retried, as a share of all attempts. |
| p50 to p99.9, max | Time of a unit of work including its retries, within about 6%. |
| Anomalies | Violations the workers saw. |
| Invariant | The check on the reopened database, plus the anomaly count where anomalies are not allowed. |
| CPU cores, CPU/op | Process CPU time during the job, including background compaction and GC. |
| Disk writes, per commit | Process write bytes during the job. |
| Peak memory | Peak working set during the job, sampled every 100 ms. |
| GC pause | .NET pause time during the job. |
| Load, Close, Reopen, Settle, Storage | Time of each step, and the directory size after the final settle and close. |

Charts follow the viewer's light or dark color scheme, and every chart has a table with the same numbers below it.

### Limits of the numbers

- Small jobs mostly measure memory and startup. Without a warmup, a short job also pays for the .NET JIT compiling ZoneTree's code, which the native engines do not, so results at `--keys 1K` show that the benchmark works, not how the engines compare. Use `--keys 100K` or more for comparisons, and `--keys 1M` or more for steady compaction costs.
- An engine can finish a job with work left in the background, such as queued WAL records, memtables or merges. Close and Settle show that remaining work.
- Peak memory is the whole .NET process. ZoneTree's objects live on the server GC heap, which grows when memory is available, so its number is partly a GC decision. RocksDB and SQLite buffers are native. If memory matters, compare under a limit such as `DOTNET_GCHeapHardLimit`.
- Disk writes count different things per OS. Windows counts bytes the process asks to write, including those the OS caches. Linux counts bytes sent to the block layer, so short runs can show little. macOS reports n/a.
- The SQLite adapter closes its connections when the workers stop, which is when SQLite checkpoints, so its Close time is near zero and the work appears elsewhere.
- Settling is not a full compaction. RocksDB's C API has no flush for `TransactionDB`, so the pessimistic variant settles by reopening. Its old WAL files stay on disk after the reopen, and its storage number includes them, so a short run shows far more storage than the optimistic variant with the same data.
- RocksDB comes from the `RocksDbNative` 6.2.2 package, the version the profile-store benchmark uses. Newer RocksDB versions may differ.
- This benchmark does not test crash recovery. It verifies data after a clean close only.

## Policy for reference results

Reference runs set `DOTNET_TieredCompilation=0`, as in [Set up the terminal](#1-set-up-the-terminal): the whole job is measured, so without it the JIT's tiering would count against every run, more in short jobs. The report records the setting.

The reference builds up over time, so you can refresh a few scenarios without running everything again:

```bash
dotnet run -c Release -- --keys 1M --scenario transfer,order-entry --engine zonetree --update-latest
```

With `--update-latest`, each finished run goes into `reference/<os>/<preset>/<keys>/summary.json` and the report beside it is rewritten. A run replaces the earlier result of the same scenario, engine and worker count at that key count; every other result stays. So a stopped benchmark loses only the run in progress, and a run that fails or times out replaces the earlier result and shows as a problem in the report.

The report says when its runs come from different executions: the summary gives the range, each scenario page lists which execution measured which engine and worker count, and the environment section lists anything that differed, such as the ZoneTree version.

Runs compare only under the same workload, so `--update-latest` refuses to start if the job scale, value size, zipf theta, large transaction size, retry backoff, seed, engine tuning or `DOTNET_TieredCompilation` differ from the reference of a key count it would update. Use the reference's settings, delete that key count's folder to start over, or add `--force` to mix them. After `--force`, the summary has a "Mixed settings" section that lists what each execution used, and later runs must match the forced settings. Results of a scenario or engine that left the catalog are dropped at the next update.

The summary has a row for each scenario and worker count, so running at a new count adds rows and hides nothing. An engine with no result at a count has an empty cell in that row.

To rewrite a report after changing the report code, without running anything:

```bash
dotnet run -c Release -- --render-latest                # every key count in reference/<os>/standard
dotnet run -c Release -- --render-latest --preset full  # every key count in reference/<os>/full
dotnet run -c Release -- --render-report ../reference/linux/standard/1M
```

## Options

Run `--help` for the full list.

| Option | Meaning |
| --- | --- |
| `--preset smoke\|standard\|full` | smoke: 1K keys, 4 workers. standard: 100K keys, 1, 4 and 16 workers. full: 100K and 1M keys, 1 to 32 workers. |
| `--scenario <list>` | `all`, ids, or `category:<name>`. |
| `--engine <list>` | `all`, families or variant ids. |
| `--workers`, `--keys` | Comma lists. `K` and `M` suffixes work. |
| `--scale` | Multiplies every scenario's job, for example 0.1. |
| `--value-size`, `--zipf-theta`, `--large-txn-keys`, `--seed` | The workload. |
| `--retry-backoff` | Exponential randomized wait after a conflict. |
| `--timeout-seconds` | Kill a run after this time. 0 disables it. Default: 86400 (24 hours). |
| `--data`, `--output` | Directories. `--clean`, `--clean-data` and `--clean-results` empty them first. |
| `--keep-data` | Keep each run's database files, to inspect them. |
| `--resume` | Continue the latest execution with the same command: skip its finished runs, and run what is left and what failed. |
| `--update-latest` | Merge each finished run into `reference/<os>/<preset>/<keys>/` and rewrite that report. |
| `--force` | Let `--update-latest` mix runs of different settings. The report warns about it. |
| `--render-latest` | Rewrite the reports of every key count in `reference/<os>/<preset>/` from their `summary.json` files. |
| `--render-report <directory>` | The same for a report directory, or the key counts under it. |

## Adding an engine

Implement `ITransactionEngine` in `src/Engines` and add it to `EngineCatalog.All`, next to its family. Its position sets its report column and chart color, and the charts have seven slots today, so an eighth engine needs a new validated color in `SvgCharts.cs`, from its family's color range. Reads and writes report conflicts through `HasConflict` instead of throwing, so the scenarios work unchanged.

## Adding a scenario

Add a `Scenario` subclass or a new instance of an existing one to `ScenarioCatalog.All`. A scenario loads its keys, describes its job and worker roles, and checks its invariant on the reopened database. Size the job so it takes seconds on the slowest engine, and draw from `Worker.Random` only before an operation's retry loop.
