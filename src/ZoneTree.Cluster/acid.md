# ZoneTree.Cluster: cross-column ACID status

Cross-column atomicity, consistency, isolation and durability work and are tested with
simulated crashes and power losses. The crash behaviour has not been verified with real
process kills, and the failure paths have no fault-injection tests yet. So cross-column ACID
is not release-complete.

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

## Missing for full ACID as promised

1. **Not verified with real crashes.** The recovery tests simulate unclean shutdowns and
   power losses inside the process. Kill-process tests come as the final step.
2. **No fault-injection tests** for:
   - a failed domain sync leaving commits in doubt;
   - a rollback that cannot delete its records;
   - a failed catalog write of the durable sequence number.
3. **No torn power-loss tests**, which keep a random part of each unsynced write.

## Gaps to close before release

These do not break ACID:

- **In-doubt commits after a failure:** the domain's commits stay stopped until the cluster
  reopens.
- **Outcome lookups:** `GetTransactionOutcomeAsync` only asks the columns open in this
  process.
- **Unbounded crash bookkeeping:** transactions that recovery aborts after a crash are never
  purged, and the list of rolled-back ranges grows with each unclean shutdown.

The details are in [follow-up.md](follow-up.md).

## To complete it

1. Fault-injection and torn power-loss tests.
2. Kill-process crash tests.
