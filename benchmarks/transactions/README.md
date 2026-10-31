# Transaction benchmark

Compares ZoneTree, RocksDB and SQLite transactions across many use cases instead of one big workload. Each scenario answers one application question, so you can read the ones that match your workload and ignore the rest.

```bash
cd benchmarks/transactions/src

# Prove everything runs: every scenario on every engine, with small jobs.
dotnet run -c Release -- --preset smoke

# The default: 100K keys, full jobs, 1, 4 and 16 workers.
DOTNET_TieredCompilation=0 dotnet run -c Release

# Fewer keys mean smaller jobs and a shorter run.
dotnet run -c Release -- --keys 1K

# One question at a time.
dotnet run -c Release -- --list-scenarios
dotnet run -c Release -- --scenario category:contention --workers 1,2,4,8,16,32
dotnet run -c Release -- --scenario transfer,order-entry --engine zonetree,rocksdb-optimistic
```

Results go to `out/results/<stamp>/<keys>/`, for example `out/results/<stamp>/1M/`, and database files to `out/data/`. Each key count has its own report. Open its `summary.md` first: it has one comparison table and chart per category, and links to a page per scenario. Reports update after every run, so a stopped benchmark keeps what it measured.

## Engines

| Id | Engine | Isolation |
| --- | --- | --- |
| `zonetree-mvcc-ser-async`, `zonetree-mvcc-ser-sync` | ZoneTree MVCC transactional tree, serializable, async and sync WAL | A commit fails if a newer commit wrote a key it wrote or read, or a key inside a range it read. |
| `zonetree-mvcc-snap-async`, `zonetree-mvcc-snap-sync` | ZoneTree MVCC transactional tree, snapshot, async and sync WAL | A commit fails if a newer commit wrote a key it wrote or read for update. Plain reads are not protected. |
| `rocksdb-optimistic` | `OptimisticTransactionDB`, one snapshot per transaction | A commit fails if a newer commit wrote a key it wrote or read for update. Plain reads and ranges are not protected. |
| `rocksdb-pessimistic` | `TransactionDB`, key locks, deadlock detection, no snapshot in write transactions | Writes and reads for update lock the key and read the latest value. Plain reads see the latest commit, so they are not repeatable. |
| `sqlite` | WAL mode, `BEGIN IMMEDIATE` | One write transaction at a time, so writes are serializable and never conflict. Readers keep their snapshot and do not block. |

Read-only work uses each engine's snapshot: `BeginReadOnlyTransaction` on ZoneTree, an explicit snapshot on RocksDB, a deferred transaction on SQLite.

`--engine` accepts `all`, a family (`zonetree`, `rocksdb`, `sqlite`) or variant ids.

The ZoneTree `-async` ids use the `AsyncCompressed` WAL, where a background writer does the WAL work. The `-sync` ids use `SyncCompressed`, where the committing thread appends to a memory-mapped tail file, and compresses and syncs each block that fills.

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
| ZoneTree sync | `SyncCompressed` WAL | Nothing. | What the OS has not written back since the last tail sync. The tail is synced every `TailWriterJobInterval`, and each block when it reaches `CompressionBlockSize`, so at most one interval of commits, within one block. |
| ZoneTree async | `AsyncCompressed` WAL | Commits the background writer has not written to a file yet: about one `EmptyQueuePollInterval` under light load, one `CompressionBlockSize` block plus its queue under sustained load. | Commits the background writer has not synced to the disk yet: about one `EmptyQueuePollInterval` under light load, one `CompressionBlockSize` block plus its queue under sustained load. It syncs each full block and whenever its queue empties. |
| RocksDB | `sync=false` | Nothing. | What the OS has not written back since the last memtable flush. The WAL is not synced per commit or on a timer, and a flush makes its data durable, so at most the commits in the memtables: up to `max_write_buffer_number` buffers of `write_buffer_size`. |
| SQLite | `synchronous=NORMAL` | Nothing. | What the OS has not written back since the last checkpoint. A checkpoint syncs, and runs about every `wal_autocheckpoint` WAL pages, so at most about that much WAL, or more while readers hold a checkpoint back. |

`SyncCompressed`, RocksDB and SQLite all lose nothing on a process crash, and on a power loss all three lose what the OS has not written back, so a real loss is usually smaller than these limits. `AsyncCompressed` commits without waiting for the writer, which is what makes it the faster mode, so its window covers a process crash too. On every engine and mode, what survives is consistent, and an application that needs a commit to survive anything waits for it, as the `durability` category measures.

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

Reference runs set `DOTNET_TieredCompilation=0`: the whole job is measured, so without it the JIT's tiering would count against every run, more in short jobs. The report records the setting.

The reference builds up over time, so you can refresh a few scenarios without running everything again:

```bash
cd benchmarks/transactions/src
DOTNET_TieredCompilation=0 dotnet run -c Release -- --keys 1M --scenario transfer,order-entry --engine zonetree --update-latest
```

With `--update-latest`, each finished run goes into `reference/<os>/<preset>/<keys>/summary.json` and the report beside it is rewritten. A run replaces the earlier result of the same scenario, engine and worker count at that key count; every other result stays. So a stopped benchmark loses only the run in progress, and a run that fails or times out replaces the earlier result and shows as a problem in the report.

The report says when its runs come from different executions: the summary gives the range, each scenario page lists which execution measured which engine and worker count, and the environment section lists anything that differed, such as the ZoneTree version.

Runs compare only under the same workload, so `--update-latest` refuses to start if the job scale, value size, zipf theta, large transaction size, retry backoff, seed, engine tuning or `DOTNET_TieredCompilation` differ from the reference of a key count it would update. Use the reference's settings, or delete that key count's folder to start over. Results of a scenario or engine that left the catalog are dropped at the next update.

The summary compares engines at the worker count most of them ran with, the highest among equals, so rerunning one engine at a new count does not hide the comparison.

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
| `--timeout-seconds` | Kill a run after this time. Default: 600. |
| `--data`, `--output` | Directories. `--clean`, `--clean-data` and `--clean-results` empty them first. |
| `--keep-data` | Keep each run's database files, to inspect them. |
| `--update-latest` | Merge each finished run into `reference/<os>/<preset>/<keys>/` and rewrite that report. |
| `--render-latest` | Rewrite the reports of every key count in `reference/<os>/<preset>/` from their `summary.json` files. |
| `--render-report <directory>` | The same for a report directory, or the key counts under it. |

## Adding an engine

Implement `ITransactionEngine` in `src/Engines` and add it to `EngineCatalog.All`, next to its family. Its position sets its report column and chart color, and the charts have seven slots today, so an eighth engine needs a new validated color in `SvgCharts.cs`, from its family's color range. Reads and writes report conflicts through `HasConflict` instead of throwing, so the scenarios work unchanged.

## Adding a scenario

Add a `Scenario` subclass or a new instance of an existing one to `ScenarioCatalog.All`. A scenario loads its keys, describes its job and worker roles, and checks its invariant on the reopened database. Size the job so it takes seconds on the slowest engine, and draw from `Worker.Random` only before an operation's retry loop.
