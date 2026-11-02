# WAL and MVCC soak test

A standalone .NET 10 console tool for exercising Shared/AsyncShared WALs and
transactional recovery on real files. It stops on the first unexpected failure,
records a nonzero exit code, and preserves the failing case. It does not change
library settings or production code.

## Build and smoke test

From the repository root, with the .NET 10 SDK installed:

```sh
dotnet publish tools/wal-soak/WalSoak.csproj -c Release \
  --artifacts-path .tmp/wal-soak-build \
  -p:GeneratePackageOnBuild=false -o .tmp/wal-soak-publish

dotnet .tmp/wal-soak-publish/WalSoak.dll self-test \
  --root "$PWD/.tmp/wal-soak-smoke"
```

Use a fresh dedicated root. The self-test takes roughly half a minute on an SSD.
It checks the oracle's ability to reject corrupt/missing data and lost durable
receipts, then runs four real workloads with crash/reopen and durability probes.
Run it on the VPS before starting the long test. Keep the published binaries
unchanged for the lifetime of a run.

## Run on the 16 GB / 100 GB Linux VPS

The defaults reserve room for the OS and its page cache. This example runs for
14 days; each ordinary epoch runs at full speed for five minutes. Every 48th
epoch runs uninterrupted for six hours at up to 25 durable commits per second
per writer, to expose resource growth while staying within the history budget.

```sh
dotnet .tmp/wal-soak-publish/WalSoak.dll run \
  --root "$PWD/.tmp/wal-soak-14d" --duration 14d \
  --workers 8 --readers 2 \
  --epoch 5m --long-every 48 --long-epoch 6h --long-rate 25 \
  --max-rss-mb 8192 --max-disk-gb 60 --min-free-gb 15 --retain 3
```

Run it under systemd so an SSH disconnect does not stop it. An example unit is
in [zonetree-wal-soak.service](zonetree-wal-soak.service). Set its user, absolute
paths, and working directory before installing it. It deliberately does not
restart automatically after a failure. Alternatively use tmux and retain the
exit status. The data root must be on the SSD, outside tmpfs. Avoid simultaneous
benchmarks or other disk-heavy jobs on this machine.

For systemd, copy the template, edit the user and paths, then start it:

```sh
sudo cp tools/wal-soak/zonetree-wal-soak.service /etc/systemd/system/
sudo systemctl edit --full zonetree-wal-soak.service
sudo systemctl daemon-reload
sudo systemctl start zonetree-wal-soak.service
sudo journalctl -u zonetree-wal-soak.service -f
```

The service user needs write access to the data root. On subsequent starts of
the same run, add `--resume` to `ExecStart` and reload the unit first; the initial
command requires an empty root. Use `systemctl stop zonetree-wal-soak` to request
a clean stop. Operator stop (exit 130) is treated as successful by the unit.

Duration is the workload budget for an invocation: an active epoch may still
finish recovery and verification after that deadline. `--cycles N` also limits
the number of new cases. Ctrl+C/SIGTERM asks the worker to stop cleanly; the
watchdog terminates it if shutdown stalls. If verification was interrupted,
resume it before trusting the run's result.

```sh
dotnet .tmp/wal-soak-publish/WalSoak.dll run \
  --root "$PWD/.tmp/wal-soak-14d" --resume --duration 14d
```

Resume uses the original seed, workload settings, and limits; only the supplied
duration and cycle count apply to the new invocation. The root stays at its
original absolute path. Resume refuses a different tool/library build, a second
supervisor, or a root with a recorded failure. Preserve a failure and start a
new root after investigating it. After a supervisor crash, the worker observes
stdin closing and stops; resume waits for its exclusive ownership lock before
opening any files.

## Progress and failures

```sh
tail -F .tmp/wal-soak-14d/logs/progress.jsonl
tail -F .tmp/wal-soak-14d/logs/failures.log
cat .tmp/wal-soak-14d/status.json
cat .tmp/wal-soak-14d/coverage.json
```

Progress and failures have separate files:

| File | Contents |
| --- | --- |
| `logs/progress.jsonl` | Timestamped case transitions and periodic commits, RSS, handles/FDs, snapshot audits, rollbacks, cancellations, rotations, merges, and segment counts. Rotates at 16 MiB with seven archives. |
| `logs/failures.log` | Human-readable failures, full exception, and case path; fsynced when written. Created on the first failure. |
| `logs/failures.jsonl` | The same failures as structured JSON lines. |
| `status.json` | Atomic current state and latest resource/progress sample. |
| `coverage.json` | Persistent aggregate pass counts by exact configuration and actual ending, plus case IDs and the next case number. Survives case pruning and resume. |
| `run.json` | Run ID, original options, schema version, and tool/library SHA-256 fingerprints. |
| `cases/NNNNNNNN/config.json` | Per-case seed, profile, limits, and crash target. |
| `cases/NNNNNNNN/workload.json` | Actual ending, elapsed time, and acknowledged commit count. Early budget stops and supervisor interruptions are counted separately from planned crash targets. |
| `cases/NNNNNNNN/oracle.jsonl` | Independently retained issued operations, successful commits with commit timestamps, and sync acknowledgements. |
| `cases/NNNNNNNN/events.jsonl` | Child metrics, warnings, check results, and complete engine/fatal errors. Expected injected fsync errors are explicitly marked. |
| `cases/NNNNNNNN/*-child.stderr.log` | Separate child stderr. Unexpected stderr fails the case. |
| `cases/NNNNNNNN/status.json` | Case result and failure details. |

Successful cases retain their original `database/`, recovery copy, and receipts
until the retention limit removes them. Failed and interrupted cases are never
automatically pruned. Cleanup accepts only owned, validated, passed cases and
rejects symlinks/reparse points. The root must not contain unrelated files.

The disk guard samples the run size and free space every five seconds; the RSS
guard samples each child roughly five times per second. A GC heap limit at 75%
of the RSS budget leaves room for native allocations. These are watchdogs, not
OS quotas: use the systemd unit's `MemoryMax` as an additional hard boundary.
Recovery copies count toward the disk budget. File descriptor/handle counts
above 32,768 also stop the run; their trend is visible in the progress log.

| Exit | Meaning |
| --- | --- |
| 0 | All requested cases completed successfully. |
| 1 | Correctness failure, engine error, timeout, or unexpected child exit. |
| 2 | Invalid command/options. |
| 3 | Resource guard stopped the run; inspect the separately logged limit. |
| 130 | Operator requested a stop; pending cases may need resume. |

## What each case exercises

The seeded matrix mixes both shared WAL modes, None/LZ4/Zstd compression, both
disk segment formats, 1/4/128/256 KiB WAL blocks, 256 KiB/4 MiB/64 MiB chunks,
1,024/8,192-record mutable segments, and four workloads:

- `durable`: every transaction uses durable commit, with concurrent writers.
- `mixed`: ordinary and durable commits, exercising shared syncs and publication.
- `prefix`: one serial writer, permitting an exact global commit-prefix check.
- `queued`: ordinary commits with explicit syncs about every ten seconds,
  stressing async draining and rotation without a durable wait on every append.

Writers own separate deterministic account/slot lanes. Transfers preserve the
account total, write redundant mirror records, update a sequence head, and put
or delete slot records atomically. Every 97th transaction has a larger batch;
every 257th includes payloads exceeding the configured compression block.
Values alternate compressible and pseudorandom content. Both isolation levels
are used. Deliberate rollbacks include an extra key that must never be visible.
Durable waiter cancellation must resolve to a valid commit before its writer
continues. Snapshot readers compare every row with the independent model,
including repeated reads of held snapshots while merges and deletes continue.

The workload has a fixed keyspace. Explicit rotation, normal and bottom merges,
cache eviction, metadata saves, and alternating MVCC auto-sync settings run
alongside writes. The maintainer also reacts to normal automatic rotation.
`--max-outstanding 4096` bounds unacknowledged-to-disk operations per lane in the
test client, avoiding an unlimited producer overload. The async engine still
drains and rotates independently. `--max-sequence 1000000` bounds replay work
and receipt storage per lane. Reaching it ends the epoch cleanly and records a
budget event; that epoch must not be counted as a full six-hour endurance run.
Use `--long-rate` to keep long epochs under this cap.

Ordinary epochs alternate random process kills, kills immediately before and
after native WAL fsync, after durable acknowledgement, during merge publication,
after metadata rename, and clean shutdown. For controlled points the worker
signals that it reached the point and blocks until killed. A missed point fails
on timeout rather than silently claiming coverage. A queued workload substitutes
after-fsync for after-durable-ack. Random kills can interrupt arbitrary writes;
controlled before/after points do not pretend to interrupt a kernel syscall.

After the worker exits and its acknowledgements are drained, the supervisor
copies the database. A fresh child opens that copy and checks the **entire**
logical state against the seed-derived history: no partial transactions, missing
mirror records, resurrected deletions, unexpected keys, or lost acknowledged
durable operations. Shared WAL process-crash recovery must also retain every
acknowledged ordinary commit. AsyncShared may lose queued ordinary commits.
An unacknowledged in-flight commit is allowed to survive. Single-writer cases
must be an exact prefix; independent concurrent lanes must each be consistent
prefixes. Commit timestamps also check global order across concurrent lanes:
no acknowledged commit may be lost while a later acknowledged commit survives.
Unacknowledged in-flight commits have no receipt, so that cross-lane order check
covers the recorded acknowledgements. The verifier then makes new durable writes
and reopens again.

Each case additionally runs isolated native probes: a blocked fsync must keep
durable writes and their timestamp invisible, reserve conflicting keys, permit
logical rotation, and survive cancellation of the caller's wait. An injected
fsync failure must leave the commit InDoubt and invisible; earlier durable data
must survive reopening. A later ordinary commit must also wait behind the
blocked durable commit. Only that identified injected error is expected.

Finally, the existing unit-test power-loss provider captures eight reservoir-
sampled torn images across a short serial history with rotations, merges, and
metadata saves. Recovery must match a prefix including its durable floor, and
support further durable writes and reopening. A failed image is dumped under
`simulation-failure/` with its seed, configuration, and bounds.

## Inspect a case

Stop the supervisor first, then run:

```sh
dotnet .tmp/wal-soak-publish/WalSoak.dll verify \
  --case "$PWD/.tmp/wal-soak-14d/cases/00000042"
```

This creates another recovery copy; it preserves the original crashed database.
A seed reproduces the workload and sampling decisions, not OS scheduling or
an identical random-crash instruction boundary. Include `run.json`, the full
case directory, and failure logs when reporting an issue.

## Scope

Process kills exercise the real Linux file and mapped-tail paths but do not
simulate power loss. The torn-image simulator models old/new 512-byte sectors
and file sizes, while creation/deletion/rename are immediately durable in that
simulator. Real namespace ordering is exercised by native checkpoint crash
points, not by emulating every filesystem/device failure mode. An actual VM
power-cut test needs a separate host-controlled harness and durable oracle.

The supervisor's receipt file is flushed to the OS per message, not fsynced per
commit. It is fsynced after a controlled worker crash. If the whole VPS loses
power, resume checks the surviving acknowledgement prefix and relaxes the
issued upper bound, since a lost receipt cannot prove the database invented a
commit. This is intentional and avoids adding a second disk fsync to each
measured transaction. It does not verify acknowledgements that vanished with
the host's unsynced log.

This tool focuses on the new shared WAL and MVCC behavior. Legacy WAL migration
and other library features retain their existing unit-test coverage. A passing
soak increases confidence; it cannot establish correctness for every possible
failure or replace the unit suite.
