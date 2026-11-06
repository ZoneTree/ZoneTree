# ZoneTree.Cluster follow-ups

Open work after phase 3 (ACID across the columns of a domain). Items marked
**release blocker** must be done before the cluster ships, because the cluster ships
only with full ACID guarantees.

## Maintenance, only if users need it

These cost only after an unclean shutdown and do not affect ACID.

- [ ] **Startup after a crash reads every coordinated column's WAL.** Before any column of
  the domain opens, recovery reads the live WAL segments of every coordinated column of the
  domain, including columns this process may never open and dropped columns whose storage
  remains. Merges bound that by the mutable segment sizes and the merge backlog. A column
  that opens reads its WAL again. Columns of other domains are not read, and a clean open
  reads nothing. Not measured. Ideas: skip data records cheaply, since recovery needs only
  status records, or let a column reuse what recovery read.
- [ ] **Crash bookkeeping.** It grows only with unclean shutdowns, never with traffic, and
  does not affect ACID. Plain MVCC trees have the same gap.
  - **Aborted entries:** a column's recovery keeps each transaction that did not survive as
    an in-memory aborted entry plus a pending status record, and never purges them. That is
    the transactions in flight at the crash, plus after a power loss the commits since the
    last domain sync, at most 4096. About 100 bytes each.
  - **Rolled-back ranges:** `DomainRecord.RolledBackRanges` gains 16 bytes per unclean
    shutdown, read only when a column opens.
  - **Idea for the entries:** merges already drop the versions of aborted transactions. An
    aborted transaction from before an open has versions only in segments that existed at
    that open, so once a merge leaves none of them, its entries can go at once and later
    merges can drop its pending status records. One check per merge end; old bottom
    segments, which merge rarely, delay it.
  - **The ranges:** dropping one needs proof that every column recovered past it, which costs
    a catalog write per column open after a crash. Not worth it for the bytes.

## Durability and performance

Measured with `benchmarks/domain-transactions` (keep runs under 3 minutes).

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

## Postponed features

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
