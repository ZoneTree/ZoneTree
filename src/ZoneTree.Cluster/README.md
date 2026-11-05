# ZoneTreeCluster

Distribution is **by column**: each column is a separate ZoneTree with its own
`TKey`, `TValue`, comparer and serializers. The cluster is non-generic. Columns
may share a row-key type, but do not have to; joins are explicit application reads.

`ZoneTreeClusterFactory` opens a cluster whose columns run in this process.
A column has one of three kinds, fixed when the column is created:

| Kind | Engine | Transactions |
| --- | --- | --- |
| Coordinated | The cluster's copy of the MVCC engine | Cluster transactions across the columns of one domain |
| Standalone | The MVCC engine of a transactional ZoneTree | Its own transactions, covering only this column |
| Raw | Plain ZoneTree | None: direct reads and writes |

Coordinated columns are declared inside a **domain**. A domain has one timestamp
source and one commit protocol, and a cluster transaction begins from a domain and
spans only its columns, wherever they run. Standalone and raw columns are declared
outside domains. Column names are unique across the cluster.

## Schema

The cluster **catalog** is the single source of truth for the schema: the domains,
the columns with their kinds and encodings, and stable ids that are never reused. It
is a transactional ZoneTree in the cluster directory, and every schema change is one
durable catalog commit with its own schema epoch.

- **Declared schema.** The `Domains` and `Columns` of `ZoneTreeClusterOptions` are
  created when they are missing and checked when they exist. Declaration order does
  not matter, nothing is deleted, and the catalog may hold more than is declared.
- **Schema at run time.** `cluster.Schema` creates domains and columns while the
  cluster runs: `CreateDomainAsync`, `CreateColumnAsync`, their idempotent
  `GetOrCreate` forms, and `DescribeAsync`. A column that exists with another kind,
  domain or encoding fails with `SchemaConflictException`.
- **Dropping and renaming.** `DropColumnAsync` and `DropDomainAsync` return false for
  a missing name, so retries are safe; a domain is dropped only without columns. A
  dropped column's name is free at once, but its id is never reused. Its running raw
  operations finish, its iterators and standalone transactions end, reads and writes
  through domain transaction views of it throw `SchemaObjectDroppedException`, and a
  domain transaction that used it cannot commit any of its writes. A view operation that
  runs while the drop deletes the storage may fail with another exception. Its handles
  throw `SchemaObjectDroppedException`, and the task completes once its storage is deleted.
  `RenameColumnAsync` and `RenameDomainAsync` keep handles, transactions and data. A
  declaration of a dropped or old name creates a new, empty column on the next open.
- **Opening columns.** A column opens when it is declared or created in this process,
  or when it is first requested with its types, such as `GetRawColumn<TKey, TValue>`.
- **Encodings.** Definitions name their key serializer, value serializer and comparer
  by id in `ZoneTreeClusterOptions.Encodings`. Built-in ids, such as `zonetree.int64`
  and `zonetree.string.ordinal.descending`, cover the types ZoneTree supports by
  default and are used when a definition names none. Custom encodings are registered
  with an id and a version; a column opens only with the versions it was created with.

The goals are high performance, feature parity with single-tree MVCC, and one
algorithm across in-process, cross-process and cross-machine distribution.

## Clients and hosts

Applications use `IZoneTreeCluster`: domains, columns, transactions and sync. The
same client API works for a cluster in this process and for one hosted by other
processes or machines, which `ZoneTreeClusterFactory.ConnectAsync` connects to.
The process that owns the storage holds an `IZoneTreeClusterHost`, which the
in-process factory methods return. The host is also a client, and adds storage
maintenance, read-only, automatic syncing, backups and drop.

A column's registered encodings are the schema that hosts and clients share.
`ConfigureStorage` tunes the storage on the host and cannot replace them.

The cluster supports typed columns, reads and writes, rollback, conflict
preflight, ordinary and durable commits, iteration, reopening and maintenance.
A domain transaction commits atomically across its columns: it takes one commit
timestamp, readers see it in every column at once, and recovery keeps it whole. A
conflict or a failure before the commit completes rolls back every column. New
columns default to their own Shared WAL; a domain syncs its columns together.

## Current limitations

- An ordinary commit survives a process crash only once its domain synced. After an
  unclean shutdown, recovery rolls back every commit the domain had not synced, in
  every column, so a prefix of the commits survives with every transaction whole.
  Durable commits, `SyncWALAsync` and automatic syncing are not affected. A domain
  also syncs on its own after 4096 commits.
- A durable commit waits for a sync of each column the domain wrote since its last
  sync, then for a catalog commit. Concurrent durable commits share both.
- Cluster-wide backup and restore throw `NotSupportedException`.

The async data methods of column views and raw columns run on the thread pool, so
operations run in parallel, on one view or on different columns. The caller decides
the order: await an operation before starting one that depends on it.

## Usage

`ConfigureStorage` reuses the existing typed factory for compression, cache, WAL and
segment tuning.
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

  // The in-process factory returns the host, which owns the storage.
  using var cluster = factory.OpenOrCreateInProcess(options);
  using var maintainer = cluster.CreateMaintainer();

  // Resolve and type-check handles once; reuse them across transactions/threads.
  var accounts = cluster.GetDomain("accounts");
  var balances = accounts.GetColumn<long, long>("balances");
  var names = accounts.GetColumn<long, string>("names");
  var audit = accounts.GetColumn<Guid, string>("audit");
  var visits = cluster.GetStandaloneColumn<long, long>("visits");
  var cache = cluster.GetRawColumn<string, Memory<byte>>("cache");

  // A standalone column has its own transactions, which are also its column view.
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

  // Create a domain and its columns while the cluster runs. GetOrCreate is idempotent.
  var orders = await cluster.Schema.GetOrCreateDomainAsync("orders");
  var lines = await cluster.Schema.GetOrCreateColumnAsync("orders",
      new CoordinatedColumnDefinition<Guid, string>("lines"));
  await orders.ExecuteAsync(async (transaction, token) =>
      await transaction.GetColumn(lines).UpsertAsync(Guid.NewGuid(), "2 x coffee", token));

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

`BeginTransaction(new ClusterTransactionOptions { Timeout = TimeSpan.FromSeconds(30) })`
rolls back a transaction that is still active when the timeout elapses, so a forgotten
transaction does not keep the versions its snapshot can read. Its later operations and
its commit throw `TransactionAbortedException`; a commit that started first is not
affected. The options also carry the isolation level, and work for domains and
standalone columns alike. The retry helpers take the same options and apply them to
each attempt; an attempt that times out throws and is not retried.
`BeginReadOnlyTransaction(new ClusterReadTransactionOptions { Timeout = ... })` ends a
forgotten read-only transaction the same way; its later reads throw `ObjectDisposedException`.

## Target API guarantees

The interfaces describe the guarantees the cluster is built to provide.
[Current limitations](#current-limitations) lists what is not implemented yet.

| Operation | Durability guarantee |
| --- | --- |
| `Commit()` / `TryCommit()` | Atomic commit across columns; survives a process crash. |
| `CommitAsync()` / `TryCommitAsync()` | The same process-crash guarantee, with asynchronous completion. |
| `CommitDurableAsync()` / `TryCommitDurableAsync()` | Atomic commit across columns; survives power loss or an OS crash. |
| `SyncWALAsync()` | Makes completed raw writes and transactions committed before the call durable across power loss or an OS crash. |

Transactions accept `Serializable` and `Snapshot` isolation across the columns of
their domain. A transaction reads one snapshot of the domain in every column, and a
serializable commit validates its reads in every column, including columns it did
not write.

Raw columns expose direct sync/async reads, writes, deletes, ranges and iterators.
Raw and standalone handles cannot be passed to `transaction.GetColumn(...)`. Raw writes are
independent of transaction snapshots, commit and rollback, and use plain ZoneTree's
value/deletion settings. Raw handles are thread-safe; dispose their iterators when done.
`IsReadOnly` prevents raw writes and new writing transactions. Cluster-wide
`SyncWALAsync`, automatic syncing and maintenance include every kind of column.

The catalog stores each column's kind, domain and encodings. Declaring or creating a
column with others fails with `SchemaConflictException`.

Column views, and the operations of each view, may be used concurrently. A commit or
rollback waits for the running operations, and one that starts while the transaction
ends throws, so await all outstanding operations before ending the transaction.
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

`Execute`, `ExecuteAsync` and `ExecuteDurableAsync` work for domains and standalone
columns alike. A transaction whose commit ends in doubt can be looked up later with
`GetTransactionOutcomeAsync`.

Cluster and column maintenance are available on the host. The backup, restore and
connect APIs are defined, but they throw `NotSupportedException` until they are implemented.
