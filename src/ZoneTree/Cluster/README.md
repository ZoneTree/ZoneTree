# ZoneTreeCluster

Distribution is **by column**: each column is a separate ZoneTree with its own
`TKey`, `TValue`, comparer and serializers. The cluster is non-generic. Columns
may share a row-key type, but do not have to; joins are explicit application reads.

`ZoneTreeClusterFactory` opens a cluster whose columns run in this process.
A column has one of three kinds, fixed when the cluster is created:

| Kind | Engine | Transactions |
| --- | --- | --- |
| Coordinated | The cluster's copy of the MVCC engine | Cluster transactions across the columns of one domain |
| Standalone | The MVCC engine of a transactional ZoneTree | Its own transactions, covering only this column |
| Raw | Plain ZoneTree | None: direct reads and writes |

Coordinated columns are declared inside a **domain**. A domain has one timestamp
source and one commit protocol, and a cluster transaction begins from a domain and
spans only its columns, wherever they run. Standalone and raw columns are declared
outside domains. Column names are unique across the cluster.

The goals are high performance, feature parity with single-tree MVCC, and one
algorithm across in-process, cross-process and cross-machine distribution.

The cluster supports typed columns, reads and writes, rollback, conflict
preflight, ordinary and durable commits, iteration, reopening and maintenance.
A detected conflict is rejected before any column commits. Durability follows
each column's WAL configuration; new columns default to their own Shared WAL.

## Current limitations

- Snapshots and isolation are per column, taken when a transaction first reads
  or writes the column. There is no shared snapshot across a domain's columns.
- Commits across columns are not atomic for visibility or crash recovery. An
  error after column commits start may leave some columns committed; the
  transaction then reports `InDoubt` and throws rather than reporting a conflict.
- Domain timestamps are kept in memory and start again when the cluster reopens.
- Cluster-wide backup and restore throw `NotSupportedException`.
- Columns and domains cannot be added after the cluster is created.

The async data methods of coordinated column views and raw columns run on the thread
pool, so operations on different columns run in parallel. The operations of one
coordinated column view run one at a time, in call order.

## Usage

`ConfigureStorage` reuses the existing typed
factory for serializers, comparers, compression, cache and segment tuning.
Optional `StorageDefaults` applies to every column, of every kind, when
creating or opening it. Each column's `ConfigureStorage` runs afterward, allowing
overrides. The configurator's generic method works with every column's key/value types.

```csharp
using ZoneTree;
using ZoneTree.Transactional;
using ZoneTree.Cluster;

static async Task Example()
{
  var factory = new ZoneTreeClusterFactory();
  var options = new ZoneTreeClusterOptions
  {
    DataDirectory = "./bank",
    StorageDefaults = new BankStorageDefaults(),
    // Coordinated columns are declared in a domain; transactions span one domain.
    Domains =
    [
      new DomainDefinition("accounts")
      {
        Columns =
        [
          new CoordinatedColumnDefinition<long, long>("balances")
          {
            ConfigureStorage = storage => storage.SetMutableSegmentMaxItemCount(1_000_000)
          },
          new CoordinatedColumnDefinition<long, string>("names"),
          new CoordinatedColumnDefinition<Guid, string>("audit")
        ]
      }
    ],
    Columns =
    [
      new StandaloneColumnDefinition<long, long>("visits"),
      new RawColumnDefinition<string, Memory<byte>>("cache")
    ]
  };

  using var cluster = factory.OpenOrCreateInProcess(options);
  using var maintainer = cluster.CreateMaintainer();

  // Resolve and type-check handles once; reuse them across transactions/threads.
  var accounts = cluster.GetDomain("accounts");
  var balances = accounts.GetColumn<long, long>("balances");
  var names = accounts.GetColumn<long, string>("names");
  var audit = accounts.GetColumn<Guid, string>("audit");
  var visits = cluster.GetStandaloneColumn<long, long>("visits");
  var cache = cluster.GetRawColumn<string, Memory<byte>>("cache");

  // A standalone column is a transactional ZoneTree, with its own transactions and helpers.
  visits.Execute(transaction =>
  {
    transaction.TryGet(1L, out var count);
    transaction.Upsert(1L, count + 1);
  });

  // Raw writes take effect immediately, independently of transaction commit/rollback.
  await cache.UpsertAsync("welcome", new byte[] { 1, 2, 3 });
  var cached = await cache.TryGetAsync("welcome");

  using (var transaction = accounts.BeginTransaction()) // Serializable by default.
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
  await accounts.ExecuteDurableAsync(async (transaction, token) =>
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

  using (var snapshot = accounts.BeginReadOnlyTransaction())
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
  await cluster.SyncWALAsync();

}

sealed class BankStorageDefaults : IColumnStorageConfigurator
{
  public void Configure<TKey, TValue>(ColumnDefinition column, ZoneTreeFactory<TKey, TValue> storage)
  {
    storage.SetMutableSegmentMaxItemCount(500_000)
        .ConfigureDiskSegmentOptions(options => options.KeyCacheSize = 4096);
  }
}
```

An explicit transaction also provides `TryCommit()`, `CommitDurableAsync()` and
`TryCommitDurableAsync()`. `false` means conflict and whole-transaction rollback;
storage errors throw. Dispose an active transaction to roll it back. Column views
are borrowed: only the cluster transaction commits, rolls back or owns a snapshot.

## Target API guarantees

The interfaces describe the guarantees the cluster is built to provide.
[Current limitations](#current-limitations) lists what is not implemented yet.

| Operation | Durability guarantee |
| --- | --- |
| `Commit()` / `TryCommit()` | Atomic commit across columns; survives a process crash. |
| `CommitAsync()` / `TryCommitAsync()` | The same process-crash guarantee, with asynchronous completion. |
| `CommitDurableAsync()` / `TryCommitDurableAsync()` | Atomic commit across columns; survives power loss or an OS crash. |
| `SyncWALAsync()` | Makes completed raw writes and transactions committed before the call durable across power loss or an OS crash. |

Transactions accept `Serializable` and `Snapshot` isolation. They currently apply
within each coordinated column; isolation across a domain's columns is not
implemented yet.

Raw columns expose direct sync/async reads, writes, deletes, ranges and iterators.
Raw and standalone handles cannot be passed to `transaction.GetColumn(...)`. Raw writes are
independent of transaction snapshots, commit and rollback, and use plain ZoneTree's
value/deletion settings. Raw handles are thread-safe; dispose their iterators when done.
`IsReadOnly` prevents raw writes and new writing transactions. Cluster-wide
`SyncWALAsync`, automatic syncing and maintenance include every kind of column.

Column kind is stored with the schema. Reopening with a different kind fails.

Different coordinated column views may be used concurrently. Use the synchronous methods
of each view sequentially; its async methods are queued in call order. Await all
outstanding operations before ending the transaction.
The example starts independent column writes together and awaits both before commit.

Async point methods return `ValueTask`. `TryGetAsync` and `TryGetForUpdateAsync`
return `ReadResult<TValue>` with `Found` and `Value` properties.
Ranges and iterators also have asynchronous APIs:

```csharp
await foreach (var entry in amounts.RangeAsync(1L, 100L)) { /* Ordered records. */ }
await using var iterator = amounts.CreateAsyncReverseIterator();
await iterator.SeekAsync(100L);
while (await iterator.NextAsync()) { /* Descending records. */ }
```

`Execute`, `ExecuteAsync` and `ExecuteDurableAsync` provide conflict retries.
Coordinated column convenience methods include `TryGet`, `ContainsKey`, `Upsert`,
`TryDelete` and `AddOrUpdate`, plus async versions. Each convenience call uses its
own transaction. Raw column operations do not create transactions.

Cluster and column maintenance are available. The backup and restore API is
defined, but its methods throw `NotSupportedException` until they are implemented.
