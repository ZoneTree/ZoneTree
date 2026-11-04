# Distributed database API proposal

Distribution is **by column**: each column is a separate ZoneTree with its own
`TKey`, `TValue`, comparer and serializers. The database is non-generic. Columns
may share a row-key type, but do not have to; joins are explicit application reads.

This change defines compilable contracts and convenience helpers. The factory,
coordinator, recovery and distributed backup runtime are **not implemented yet**.
`DistributedMvccZoneTree<TKey, TValue>` remains the copied MVCC engine to evolve
into a column participant. The existing single-tree MVCC implementation is unchanged.

## Usage

The example accepts the proposed factory; a concrete in-process factory comes
with the runtime implementation. `ConfigureStorage` reuses the existing typed
factory for serializers, comparers, compression, cache and segment tuning.

```csharp
using ZoneTree;
using ZoneTree.Backup;
using ZoneTree.Transactional;
using ZoneTree.Transactional.Distributed;

static async Task Example(IDistributedDatabaseFactory factory)
{
  var options = new DistributedDatabaseOptions
  {
    DataDirectory = "./bank",
    Columns =
    [
      new DistributedColumnDefinition<long, long>("balances")
      {
        ConfigureStorage = storage => storage.SetMutableSegmentMaxItemCount(1_000_000)
      },
      new DistributedColumnDefinition<long, string>("names"),
      new DistributedColumnDefinition<Guid, string>("audit")
    ]
  };

  using var database = factory.OpenOrCreateInProcess(options);
  using var maintainer = database.CreateMaintainer();

  // Resolve and type-check handles once; reuse them across transactions/threads.
  var balances = database.GetColumn<long, long>("balances");
  var names = database.GetColumn<long, string>("names");
  var audit = database.GetColumn<Guid, string>("audit");

  using (var transaction = database.BeginTransaction()) // Serializable by default.
  {
    var amounts = transaction.GetColumn(balances);
    var labels = transaction.GetColumn(names);
    amounts.Upsert(1L, 10_000L);
    labels.Upsert(1L, "Alice");
    transaction.Commit(); // Both columns commit together; survives process crash.
  }

  // Retry the entire transaction on conflicts. No external side effects in the body.
  await database.ExecuteDurableAsync(transaction =>
  {
    var amounts = transaction.GetColumn(balances);
    var events = transaction.GetColumn(audit);
    if (!amounts.TryGet(1L, out var balance) || balance < 500L)
      throw new InvalidOperationException("Insufficient balance.");
    amounts.Upsert(1L, balance - 500L);
    events.Upsert(Guid.NewGuid(), "Withdrawal: 500");
  }, maxAttempts: 10); // Syncs before publication; survives power loss / OS crash.

  using (var snapshot = database.BeginReadOnlyTransaction())
  {
    var amounts = snapshot.GetColumn(balances);
    var labels = snapshot.GetColumn(names);
    labels.TryGet(1L, out var name);
    amounts.TryGet(1L, out var balance); // Same snapshot as the name, even if bound later.

    foreach (var entry in amounts.Range(1L, 100L)) { /* Ordered, lazy range. */ }
    using var iterator = amounts.CreateReverseIterator(
        new TransactionIteratorOptions { DiskSegmentPrefetchSize = 64 });
    iterator.Seek(100L);
    while (iterator.Next()) { /* Descending keys. */ }
  }

  // Batch ordinary commits, then make all previously published commits durable.
  await database.SyncWALAsync();

  using var backup = database.CreateLiveBackup(
      new LocalLiveBackupOptions { Directory = "./bank-backup", KeepLastGenerations = 3 });
  await backup.CreateGenerationAsync();
}
```

An explicit transaction also provides `TryCommit()`, `CommitDurableAsync()` and
`TryCommitDurableAsync()`. `false` means conflict and whole-transaction rollback;
storage errors throw. Dispose an active transaction to roll it back. Column views
are borrowed: only the database transaction commits, rolls back or owns a snapshot.

## Guarantees and performance constraints

| Operation | Publication and durability contract |
| --- | --- |
| `Commit()` / `TryCommit()` | All column writes become visible together after required data and the commit decision reach the OS. Survives process crash; no power-loss sync is requested. |
| `CommitDurableAsync()` / `TryCommitDurableAsync()` | Syncs required column data, recovery dependencies and the decision before publication. Survives power loss / OS crash. Concurrent commits can share syncs. |
| `SyncWALAsync()` | Makes the complete database commit prefix published before the call durable, including all its columns and decisions. |

The initial in-process factory uses ordered **Shared WALs** for columns and the
coordinator. Configurations that weaken these guarantees, including queued writes
without an OS-append barrier or disabled WALs, are rejected. This deliberately
makes ordinary commit's process-crash guarantee explicit. The single MVCC factory's
asynchronous WAL modes can lose queued writes on process crash. Storage must honor
flushes for the durable guarantee, as it must for the single MVCC tree.

One database snapshot covers every column, including columns accessed later.
Serializable validation spans all read keys and ranges when any column is written;
`Snapshot` isolation and `TryGetForUpdate` retain their single-tree meanings across
columns. Equal keys in different columns never conflict merely because they are equal.
Read-only transactions never conflict and collect no read set.

The runtime must preserve these properties:

- Direct typed calls for in-process reads/writes: no transport serialization,
  per-record name lookup, boxing of keys/values or mandatory asynchronous wrapper.
- Lazy column enlistment and cached views; beginning a transaction does not open
  a transaction in every column. Global snapshot protection still starts at begin.
- Stream versions into each column as writes happen; retain conflict metadata,
  rather than buffering all values until commit. Coordinate only accessed columns
  and required recovery dependencies. Unrelated columns do not perform per-commit I/O.
- One recoverable outcome and publication point for the entire transaction. Recovery,
  version reclamation and backups must understand database decisions. Calling the
  current independent tree commits in sequence cannot implement this contract.
- Keep single-column transactions on a fast path and batch durability syncs where
  ordering permits. Performance is a design constraint here, not a measured claim.

An uncertain commit is `InDoubt`, stays invisible in that instance and cannot be
rolled back. Recovery determines its outcome across all columns before reopening.
Cancelling a started durable commit only stops waiting; it may still commit.
Retry helpers retry conflicts only, never cancellation or uncertain/storage failures.

## Other entry points

`IDistributedDatabaseFactory` also defines create-only, open-only, and restore of the
latest or a selected backup generation into an empty destination. Backups include
schema, coordinator state and all columns at one consistent snapshot; separate
column backups cannot be combined into a database backup. Local retention and custom
`IBackupObjectStorage` providers are exposed, along with existing backup scheduling,
compression, progress and lifecycle APIs.

`database.Maintenance` exposes database counters, metadata saving and whole-database
drop. `column.Maintenance` exposes per-column storage diagnostics; column maintainers
allow separate tuning. Raw column mutations or independent commits bypass coordination
and are unsupported. Schema and membership are fixed in this first version.

`database.Execute` / `ExecuteDurableAsync` and column `TryGet`, `ContainsKey`, `Upsert`,
`TryDelete`, `AddOrUpdate` preserve the existing convenience operations. For throughput,
bind views once and batch work inside an explicit database transaction.

Only in-process creation/opening is proposed now. Cross-process and cross-machine
placement will add transport/connection entry points around the **same participant
and coordinator protocol**. Replication, failover and online column relocation need
separate designs; they are not implied by this initial API.
