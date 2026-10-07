# Transactions

Transactions coordinate changes across multiple keys. Use them when a workflow must read or write several keys as one logical unit.

For simple high-throughput writes, prefer the non-transactional API.

## Open A Transactional Tree

```csharp
using ZoneTree;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/tx")
    .OpenOrCreateTransactional();
```

## Basic Transaction

```csharp
var tx = zoneTree.BeginTransaction();

zoneTree.Upsert(tx, 1, "first");
zoneTree.Upsert(tx, 2, "second");

var result = zoneTree.PrepareAndCommit(tx);

Console.WriteLine(result.Result);
```

`Prepare` and `PrepareAndCommit` report commit state:

| Result | Meaning |
| --- | --- |
| `Committed` | the transaction committed |
| `ReadyToCommit` | `Prepare` succeeded and a separate `Commit` call can finish it |
| `PendingTransactions` | this transaction depends on uncommitted transactions |
| `Aborted` | the transaction was aborted |

## When To Use Transactions

Use transactions for:

* moving value from one key to another,
* updating a primary record and secondary index together,
* ensuring multiple keys change together,
* checking multiple keys before writing.

Do not use transactions merely because writes are concurrent. Regular writes are designed for concurrent workloads.

## Atomic Methods Vs Transactions

| Requirement | Use |
| --- | --- |
| Current value of one key decides the next value | atomic method |
| Multiple keys must commit together | transaction |
| Simple replace/add/delete | regular write API |

## Keep Transactions Short

Keep transaction work focused. Long-running transactions increase conflict windows and can make other transactions wait or retry.

## No-Throw APIs

Most transactional APIs have `NoThrow` variants. Read/write methods return `TransactionResult` or `TransactionResult<T>` instead of throwing `TransactionAbortedException`; prepare and commit methods return `CommitResult`.

```csharp
var tx = zoneTree.BeginTransaction();

var write = zoneTree.UpsertNoThrow(tx, 1, "value");
if (write.IsAborted)
{
    return;
}

var commit = zoneTree.PrepareAndCommitNoThrow(tx);
if (commit.IsPendingTransactions)
{
    Console.WriteLine("Waiting on: " +
        string.Join(", ", commit.PendingTransactionList));
}
```

## Fluent Transactions

`BeginFluentTransaction` wraps retry handling for aborted and pending transactions.

```csharp
using var tx = zoneTree.BeginFluentTransaction();

var result = await tx
    .Do(id => zoneTree.UpsertNoThrow(id, 1, "first"))
    .Do(id => zoneTree.UpsertNoThrow(id, 2, "second"))
    .CommitAsync();

Console.WriteLine(result.Succeeded);
```

The fluent API is useful when retrying a short unit of work is easier than managing prepare, pending dependencies, and rollback directly.

## Read-Committed Reads

Transactional trees expose read-committed helpers for reading committed data without starting a new transaction.

```csharp
if (zoneTree.ReadCommittedTryGet(1, out var value))
{
    Console.WriteLine(value);
}
```

Pass the current transaction id when a transaction should see its own uncommitted writes and committed writes from others.

## Auto-Commit Helpers

`UpsertAutoCommit` and `DeleteAutoCommit` start a transaction, perform one write, and commit it. They are convenient when all writes must pass through the transaction log, but they are heavier than regular `Upsert` and `ForceDelete`.

## Transaction Maintenance

Transactional maintenance exposes the underlying tree and transaction log:

* `zoneTree.Maintenance.ZoneTree`,
* `zoneTree.Maintenance.TransactionLog`,
* `zoneTree.Maintenance.UncommittedTransactionIds`.

Roll back abandoned transactions during operational cleanup:

```csharp
zoneTree.Maintenance.RollbackUncommittedTransactionIdsBefore(
    DateTime.UtcNow.AddMinutes(-10));
```
