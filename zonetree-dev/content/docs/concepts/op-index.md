# Operation Indexes

An operation index is ZoneTree's producer write sequence number. Every successful write receives an increasing `opIndex`.

That sequence is useful whenever writes need to be replayed, audited, restored, replicated, or applied idempotently.

## Write Sequence

```text
Upsert(A)      -> opIndex 10
Upsert(B)      -> opIndex 11
ForceDelete(A) -> opIndex 12
Upsert(A)      -> opIndex 13
```

The operation index records the order in which ZoneTree accepted those writes from the producer.

`Upsert` can also receive the assigned operation index before the value is created:

```csharp
var opIndex = zoneTree.Upsert(
    key,
    opIndex => new AuditValue(opIndex, payload));
```

This is useful when the value itself should carry the write sequence.

## Recovery

WAL entries carry operation indexes. During recovery, ZoneTree reads WAL records with their original sequence numbers and restores the producer high-water mark.

After restart, new writes continue from a safe operation index instead of reusing older sequence numbers.

## Replay

Operation indexes make replay streams stable.

```text
key A, opIndex 10
key B, opIndex 11
key A, opIndex 12
```

A consumer can process the stream in operation-index order and reproduce the producer write order.

For idempotent apply, a consumer can also remember the latest operation index it has accepted for each key. If an older operation for the same key arrives later, the consumer can ignore it.

```text
applied: key A, opIndex 12
incoming: key A, opIndex 10  -> already superseded
```

## Replication

`Replicator<TKey, TValue>` uses this pattern. It keeps a companion ZoneTree of latest operation indexes by key. Incoming upserts are applied to the replica only when their operation index is fresh enough for that key.

This makes replicated upserts naturally idempotent for same-key updates while preserving the producer's write sequence for audit and replay.
