# ZoneTree.Cluster: cross-column ACID status

Cross-column atomicity, consistency, isolation and durability work and are tested with real
process kills, simulated crashes, torn power losses and injected failures.

## Done and tested

- **Atomic commits:** a transaction commits in every column it wrote or in none. A conflict,
  a failed write, a dropped column or a failure while preparing rolls back every column.
- **Atomic visibility:** a transaction reads one snapshot of the whole domain, so a reader
  sees a commit in all its columns or in none. A concurrent test checks this.
- **Isolation across columns:** serializable transactions validate their reads in every
  column, including columns they did not write. A test shows that write skew across columns
  is rejected. Snapshot isolation is also available.
- **Recovery keeps a prefix of the commits:** the domain numbers its commits without gaps,
  and each commit record holds the commit's number and the number of columns the
  transaction wrote. After an unclean shutdown, recovery reads the commit records in every
  coordinated column's WAL and keeps the commits before the first number it cannot find
  complete, in every column. This is the prefix one tree keeps:
  - with the `Shared` journal, every commit that returned survives a process crash;
  - a commit lost from every column leaves its number missing, so no later commit survives
    without it, even one that read it in another column;
  - columns opened lazily sessions later roll back the same commits.

  Power-loss tests with `PowerLossFileStreamProvider` cover complete commits that were never
  synced as a domain, an incomplete commit, a commit lost from every column, and a lazily
  opened column.
- **Failures after the commit number:** a commit that fails after it took its number stops
  the domain's commits until the cluster reopens, so no later commit becomes visible. A test
  covers a column that refuses the commit record.
- **Merges and drops keep recovery whole:** before a merge moves a column's commit records
  out of its WAL, the domain syncs and the column records its durable watermark. Before a
  coordinated column's storage is deleted, the domain syncs and records how far its commits
  are durable in the catalog. Tests cover both.
- **Durable commits:** `CommitDurableAsync` survives a power loss. It waits for one parallel
  round of syncs of the columns the domain wrote, which covers every earlier commit.
- **Torn power losses:** a workload of ordinary and durable commits across two columns, with
  merges, takes a torn power-loss image at every disk sync, the catalog included. Every
  image recovers with every durable commit, the same prefix in both columns, and accepts
  new commits. Tests cover the `Shared` and `AsyncShared` journals.
- **Injected failures:** tests cover:
  - a commit record write that fails in the second column after the first wrote its own;
  - commits waiting behind a failed commit, which end in doubt and roll back;
  - a failed domain sync, after which the durable commit is in doubt and recovery keeps it
    in both columns after a process crash, or in neither after a power loss;
  - a failed catalog write during a drop, after which the column stays for recovery;
  - a failed catalog write at close, which leaves the domain to recovery.
- **Process kills:** `ClusterCrashTests` kills a worker process 12 times on the same data,
  with the `Shared` and the `AsyncShared` journal. Some kills land during recovery. The
  worker runs a bank workload with only the public API:
  - four threads commit transfers across four coordinated columns, and every 32nd commit
    of each thread is durable;
  - small mutable segments keep rotations and merges running;
  - a coordinated column is created and dropped in a loop;
  - a raw and a standalone column take writes.

  Each commit that returns appends an acknowledgement to a file. After every kill, the next
  process checks the data right after its recovery, and the test checks it again at the end:
  - every acknowledged commit survives with `Shared`, and every durable one with either
    journal;
  - each transfer is whole in all four columns, and the balances match the ledger;
  - a commit survives only with every commit that returned before it began;
  - what an earlier recovery found stays;
  - the created and dropped column is whole or gone, and the standalone column keeps its
    transactions whole.

## Maintenance

Crash bookkeeping, the aborted entries and rolled-back ranges each unclean shutdown leaves,
is never purged. It is small and does not affect ACID, so it is maintenance work for when
users need it. The details are in [follow-up.md](follow-up.md).
