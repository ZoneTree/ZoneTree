# ZoneTree.Cluster follow-ups

Open work after phase 3 (ACID across the columns of a domain). Items marked
**release blocker** must be done before the cluster ships, because the cluster ships
only with full ACID guarantees.

## Recovery

- [ ] **Purge crash-aborted transactions.** Recovery turns each rolled-back commit into
  a pending status record plus an aborted entry, and neither is ever purged. Their
  versions merge away, but the entries stay in memory and the records stay on disk,
  across restarts.
- [ ] **Recovery reads every coordinated column's WAL.** At the open after an unclean
  shutdown, recovery reads the live WAL segments of every coordinated column of the
  domain, including dropped columns whose storage remains. Merges keep that bounded by the
  mutable segment sizes. Columns of other domains are not read.
- [ ] **Compact the rolled-back ranges.** `DomainRecord.RolledBackRanges` gains one range
  per unclean shutdown and is never compacted. A range can be dropped once every column
  of the domain has recovered it, and adjacent ranges can be merged.
- [ ] **Resolve in-doubt commits.** A failed domain sync stops the domain's commits until
  the cluster reopens, and recovery decides the outcome. The same happens when a commit
  fails after it took its sequence number, such as on a WAL write error: the commits behind
  it end in doubt. Consider a path that resumes
  commits without a reopen.

## Durability and performance

Measured with `benchmarks/domain-transactions` (keep runs under 3 minutes).

- [ ] **Engine: segment rotation stalls writers.** `MoveMutableSegmentForward` appends to
  the meta WAL with an fsync on the writing thread, while the tree's other writers spin on
  the frozen segment. Columns with small mutable segments rotate often, so this dominates
  writes with many columns and a shared memory budget: up to a third of a writer's time.
  This lives in `src/ZoneTree`.

  A rotation appends three meta WAL records (maximum op index, read-only segment, new
  mutable segment), each with its own fsync, under `AtomicUpdateLock`. Skipping those three
  fsyncs, which is unsafe on power loss, measures what they cost:
  - **Setup:** `benchmarks/domain-transactions`, 100K records over 8 logical columns,
    100-byte values, AsyncShared WAL, serializable isolation. Each measurement runs
    0.5 seconds of warmup, then counts transactions for 1 second.
  - **Mutable segments:** a budget of 250K records per engine. `mvcc-one-tree` and
    `cluster-one-column` have one tree of 250K records. `mvcc-trees` and `cluster-columns`
    have 8 trees of 31,250 records each, so they rotate about 8 times as often.

  | Workload | Threads | `cluster-columns`, with fsyncs | without | `mvcc-trees`, with fsyncs | without |
  | --- | --- | --- | --- | --- | --- |
  | write, span 1 | 1 | 373K | 592K (1.59×) | 416K | 706K |
  | write, span 1 | 16 | 540K | 851K (1.58×) | 735K | 1.31M |
  | write, span 4 | 1 | 104K | 162K (1.55×) | 137K | 179K |
  | write, span 4 | 16 | 155K | 273K (1.76×) | 172K | 357K |
  | rmw, span 1 | 16 | 537K | 907K (1.69×) | 493K | 963K |
  | rmw, span 4 | 16 | 140K | 253K (1.81×) | 165K | 309K |

  The engines with one 250K-record tree are 0–20% faster without them: they rotate rarely. Batching the
  three records under one fsync is not enough on its own: on power loss, a torn write can
  keep the new mutable segment record without the read-only one, which loses that
  segment. A safe fix writes the read-only and new-segment records, each fsynced, before
  freezing the full segment, publishes the new segment after the freeze, and writes the
  maximum op index (a hint that recovery recomputes) afterward. Writers then never wait for
  an fsync. Check first that a metadata save cannot run between the early records and the
  publish.
- [ ] **Two status writes per column.** A transaction writes a pending record and then a
  commit record in every column it writes. One tree writes two per transaction, so a
  4-column transaction writes 8 against 2. That is most of the single-thread cost of
  multi-column transactions. Fewer writes need a recovery rule for versions without a
  status record, plus pending records that the sync writes lazily for transactions the
  durable sequence number passes.
- [ ] **One total order per domain.** Every commit takes a timestamp and completes in
  order through the sequencer, and a commit returns only once it is visible. At 16
  threads, span-1 writes reach about 65% of independent trees that give no atomicity,
  and the visibility wait is the largest share. A lock-free sequencer would help, but it
  is subtle.
- [ ] **Domain registry gate.** A writer still takes the domain's snapshot gate to begin,
  and a reader takes it when the snapshot changed. Under high commit rates the snapshot
  changes on every commit, so readers in mixed workloads mostly take the slow path.
- [ ] **Per-transaction allocations.** A domain transaction allocates its view lock and
  arrays, and every view allocates a bookkeeping lock even if it only reads.
- [ ] **Ordinary commits wait behind durable ones.** An ordinary commit returns only once the
  domain snapshot reaches it, as in the engine. A durable commit in flight therefore
  delays the ordinary commits after it by its sync.

## Outcomes

- [ ] `GetTransactionOutcomeAsync` asks only the columns open in this process. A transaction
  that wrote only to unopened columns reports `Unknown`. Either open the domain's columns
  for the lookup or document it.

## Catalog

- [ ] **Missing cluster header.** A catalog without its cluster record is treated as new.
  After storage damage or an incomplete restore, ids restart, reuse existing column
  directories, and a failed declaration can drop existing data. Refuse that case. Also
  refuse a new catalog next to an existing `columns` directory, and refuse creating a column
  whose directory already exists. See the TODO in `ClusterCatalog.Open`.

## Tests

- [ ] **Fault injection:**
  - a commit record write that fails in the second column, after the first wrote its own;
  - commits in flight behind a failed commit (they must end `InDoubt`);
  - a failed domain sync (durable commits end `InDoubt`);
  - a failed catalog write of the durable sequence number.
- [ ] **Torn power-loss tests** with `PowerLossFileStreamProvider.SimulateTornPowerLoss`:
  a commit reported durable survives, and recovery keeps a prefix of complete commits.
- [ ] **Crash (process kill) tests.** These come as the final step, and only on request.

## Postponed features

- [ ] **Cluster-wide backup and restore.** They throw `NotSupportedException` today, and
  `ClusterLiveBackupOptions` is only an API stub.
- [ ] **Remote and distributed.** The domain coordinator is the remote boundary, and
  `ITimestampOracle` is the clock interface. This work covers remote clients, multiple
  coordinators, and schema permissions and authentication.

## ZoneTree engine

- [ ] **Examine moving the commit record out of the MVCC tree's commit lock.**
  `MvccZoneTree.PrepareCommit` writes each commit record (`Storage.ForceDelete` of the
  pending status) while holding `CommitLock`, so every committer of the tree waits for that
  storage write, including a segment rotation's disk syncs. The cluster writes it after
  releasing its commit gate. That makes one cluster column 10–17% faster on writes than one
  MVCC tree at 16 threads (see [perf.md](perf.md)), which estimates the gain for plain
  ZoneTree.
  - **Why it is not a direct port:** the tree keeps commits in timestamp order by
    publishing them all under the lock. With the record written outside it, commits can
    finish out of order, and a new snapshot could include a later commit while an earlier
    one is missing.
  - **What it needs:** a commit sequencer that publishes commits in timestamp order, as the
    cluster's `CommitSequencer` does, plus a short wait before a commit returns, so callers
    still read their own writes. The sequencer must fit the tree's queue of pending durable
    commits.
  - **Measure first:** compare `mvcc-one-tree` with and without the change in the same run,
    writes at 1 and 16 threads, and check that 1-thread commits do not get slower.
  - **Not a source of gain:** the cluster's fetch-and-add snapshot bucket gives the MVCC
    tree no measurable read gain (11.66M txn/s at 16 threads with and without it).
