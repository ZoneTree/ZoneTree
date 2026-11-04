# Distributed database: first working slice

Distribution is **by column**: each column is a separate ZoneTree with its own
`TKey`, `TValue`, comparer and serializers. The database is non-generic. Columns
may share a row-key type, but do not have to; joins are explicit application reads.

`DistributedDatabaseFactory` opens the first working in-process implementation.
The original single-tree MVCC engine is unchanged.

The goals are high performance, feature parity with single-tree MVCC, and one
algorithm across in-process, cross-process and cross-machine distribution. The
current API starts with in-process distribution.

This slice supports typed columns, reads/writes, rollback, conflict preflight,
ordinary and durable column commits, iteration, clean reopen and maintenance.
It is experimental: snapshots and isolation are currently per column, acquired
when a column is first accessed. There is no shared cross-column snapshot,
atomic cross-column visibility, or atomic cross-column crash recovery yet.
Database timestamps are session-local bookkeeping at this stage.

A detected conflict is rejected before any column commits. An error after column
commits start may leave some columns committed; the database transaction reports
`InDoubt` and throws rather than reporting a retryable conflict. Database-wide
backup/restore currently throws `NotSupportedException`. Durability follows each
column's WAL configuration; new columns default to their own Shared WAL.

The local async data methods currently complete inline over the synchronous MVCC
engine. For actual thread parallelism, run batches on separate column workers.

## Usage

`ConfigureStorage` reuses the existing typed
factory for serializers, comparers, compression, cache and segment tuning.

```csharp
using ZoneTree;
using ZoneTree.Transactional;
using ZoneTree.Transactional.Distributed;

static async Task Example()
{
  var factory = new DistributedDatabaseFactory();
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
    // Issue independent column operations before awaiting their completion.
    // WhenAll also drains both operations if one fails.
    await Task.WhenAll(
        amounts.UpsertAsync(1L, 10_000L).AsTask(),
        labels.UpsertAsync(1L, "Alice").AsTask());
    await transaction.CommitAsync(); // Both columns commit together; survives process crash.
  }

  // Retry the entire transaction on conflicts. No external side effects in the body.
  await database.ExecuteDurableAsync(async (transaction, token) =>
  {
    var amounts = transaction.GetColumn(balances);
    var events = transaction.GetColumn(audit);
    var balance = await amounts.TryGetAsync(1L, token);
    if (!balance.Found || balance.Value < 500L)
      throw new InvalidOperationException("Insufficient balance.");
    // The balance read must finish first; these independent column writes can overlap.
    var balanceWrite = amounts.UpsertAsync(1L, balance.Value - 500L, token);
    var auditWrite = events.UpsertAsync(Guid.NewGuid(), "Withdrawal: 500", token);
    await Task.WhenAll(balanceWrite.AsTask(), auditWrite.AsTask());
  }, maxAttempts: 10); // Survives power loss / OS crash.

  using (var snapshot = database.BeginReadOnlyTransaction())
  {
    var amounts = snapshot.GetColumn(balances);
    var labels = snapshot.GetColumn(names);
    labels.TryGet(1L, out var name);
    amounts.TryGet(1L, out var balance);

    foreach (var entry in amounts.Range(1L, 100L)) { /* Ordered, lazy range. */ }
    using var iterator = amounts.CreateReverseIterator(
        new TransactionIteratorOptions { DiskSegmentPrefetchSize = 64 });
    iterator.Seek(100L);
    while (iterator.Next()) { /* Descending keys. */ }
  }

  // Batch ordinary commits, then make all previously published commits durable.
  await database.SyncWALAsync();

}
```

An explicit transaction also provides `TryCommit()`, `CommitDurableAsync()` and
`TryCommitDurableAsync()`. `false` means conflict and whole-transaction rollback;
storage errors throw. Dispose an active transaction to roll it back. Column views
are borrowed: only the database transaction commits, rolls back or owns a snapshot.

## Target API guarantees

The interfaces describe the target guarantees. The cross-column limitations of
this first implementation are listed above.

| Operation | Durability guarantee |
| --- | --- |
| `Commit()` / `TryCommit()` | Atomic commit across columns; survives a process crash. |
| `CommitAsync()` / `TryCommitAsync()` | The same process-crash guarantee, with asynchronous completion. |
| `CommitDurableAsync()` / `TryCommitDurableAsync()` | Atomic commit across columns; survives power loss or an OS crash. |
| `SyncWALAsync()` | Makes transactions committed before the call durable across power loss or an OS crash. |

Transactions accept `Serializable` and `Snapshot` isolation. In this slice these
apply within each column; full database isolation remains future work.

Different column views may be used concurrently; use each individual view
sequentially. Await all outstanding operations before ending the transaction.
The example starts independent column writes together and awaits both before commit.

Async point methods return `ValueTask`. `TryGetAsync` and `TryGetForUpdateAsync`
return `DistributedReadResult<TValue>` with `Found` and `Value` properties.
Ranges and iterators also have asynchronous APIs:

```csharp
await foreach (var entry in amounts.RangeAsync(1L, 100L)) { /* Ordered records. */ }
await using var iterator = amounts.CreateAsyncReverseIterator();
await iterator.SeekAsync(100L);
while (await iterator.NextAsync()) { /* Descending records. */ }
```

`Execute`, `ExecuteAsync` and `ExecuteDurableAsync` provide conflict retries.
Column convenience methods include `TryGet`, `ContainsKey`, `Upsert`, `TryDelete`
and `AddOrUpdate`, plus async versions. Each convenience call uses its own transaction.

Database and column maintenance are available. The backup/restore API remains
defined for a later slice.
