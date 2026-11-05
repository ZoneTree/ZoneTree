# ZoneTree.Cluster: cross-column ACID status

Cross-column atomicity, consistency and isolation work and are tested. Durability does not
yet meet what the API promises, and the crash behaviour has not been verified with real
crashes. So cross-column ACID is not release-complete.

## Done and tested

- **Atomic commits:** a transaction commits in every column it wrote or in none. A conflict,
  a failed write, a dropped column or a failure while preparing rolls back every column.
- **Atomic visibility:** a transaction reads one snapshot of the whole domain, so a reader
  sees a commit in all its columns or in none. A concurrent test checks this.
- **Isolation across columns:** serializable transactions validate their reads in every
  column, including columns they did not write. A test shows that write skew across columns
  is rejected. Snapshot isolation is also available.
- **Consistent recovery:** after any shutdown, every column keeps the same prefix of the
  domain's commits, with every transaction whole. This includes columns opened lazily
  sessions later.
- **Durable commits:** `CommitDurableAsync` survives a crash. It waits until the columns'
  WALs and the catalog are synced.

## Missing for full ACID as promised

1. **Ordinary commits do not survive a process crash.** This is the release blocker. The
   README promises that `Commit()` survives one. Today, after an unclean shutdown, recovery
   rolls back every commit the domain had not synced, in every column. That keeps
   transactions whole, but loses ordinary commits made since the last sync. A plain
   ZoneTree transaction in a synchronous WAL mode keeps them, so the cluster is weaker here.
2. **Not verified with real crashes.** The recovery tests simulate an unclean shutdown inside
   the process. Kill-process and power-loss tests have not run; they come as the final step.
3. **No fault-injection tests.** These would cover:
   - a failed domain sync leaving commits in doubt;
   - a rollback that cannot delete its records stopping the domain's commits;
   - a failed catalog write of the durable timestamp.

## Gaps to close before release

These do not break ACID:

- **In-doubt commits after a failed sync:** the domain's commits stay stopped until the
  cluster reopens.
- **Outcome lookups:** `GetTransactionOutcomeAsync` only asks the columns open in this
  process.
- **Unbounded crash bookkeeping:** transactions that recovery aborts after a crash are never
  purged, and the list of rolled-back ranges grows with each unclean shutdown.

The details are in [follow-up.md](follow-up.md), and item 1 is the release blocker in
[plan.md](plan.md).

## To complete it

1. Better recovery that keeps every complete ordinary commit after a crash.
2. Crash tests: kill-process and power-loss.
3. Fault-injection tests.
