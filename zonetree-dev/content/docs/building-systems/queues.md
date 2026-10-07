# Queues

ZoneTree can be used as the durable ordered storage layer for queue-like systems. It gives you ordered keys, fast append-style writes, range scans, deletion markers, atomic same-key counters, and optional transactions.

The queue protocol defines delivery guarantees, leases, retry policy, consumer coordination, and dead-letter handling above that storage layer.

## Key Layout

A queue needs keys that preserve the delivery order.

Common shape:

```text
queue:{queueName}:{sequence} -> payload
```

Use fixed-width sequence numbers so lexical order matches numeric order:

```text
queue:orders:00000000000000000001
queue:orders:00000000000000000002
queue:orders:00000000000000000003
```

For tenant-aware queues, put the operational boundary first:

```text
tenant:{tenantId}:queue:{queueName}:{sequence} -> payload
```

This keeps one tenant and queue adjacent in key order, which makes iterator scans simple.

## Allocating Sequence Numbers

Atomic operations are a good fit for single-key sequence counters.

```text
counter:queue:{queueName} -> nextSequence
```

Only the counter key needs atomic read-modify-write. Queue payload records can use regular `Upsert`.

```csharp
long sequence = 0;

sequenceTree.TryAtomicAddOrUpdate(
    "counter:queue:orders",
    valueToAdd: 1L,
    valueUpdater: (ref long value) =>
    {
        value++;
        return true;
    },
    result: (in long value, long opIndex, OperationResult result) =>
    {
        sequence = value;
    });

var key = $"queue:orders:{sequence:D20}";
payloadTree.Upsert(key, payload);
```

This is simple and fast. It is also a two-step workflow: sequence allocation and payload write are separate operations. If the application cannot tolerate a gap after allocating a sequence, use a transactional tree or make the queue reader tolerate missing sequence numbers.

Gaps are often acceptable in queue systems. Ordering requires monotonic keys; it does not always require dense keys.

## Reading A Queue

Consumers seek to the next known sequence and iterate forward.

```csharp
var prefix = "queue:orders:";
var startKey = $"queue:orders:{nextSequence:D20}";

using var iterator = payloadTree.CreateIterator();
iterator.Seek(startKey);

while (iterator.Next())
{
    if (!iterator.CurrentKey.StartsWith(prefix, StringComparison.Ordinal))
        break;

    Process(iterator.CurrentKey, iterator.CurrentValue);
}
```

Store consumer state separately:

```text
consumer:{consumerGroup}:{queueName} -> lastCommittedSequence
```

Updating the consumer state after processing gives at-least-once delivery. Consumers should be idempotent because a crash between processing and committing the offset can replay the same message.

## Acknowledgement Models

There is more than one valid acknowledgement model.

Use offset records when messages are immutable and consumers advance through the queue:

```text
consumer:{group}:{queue} -> lastCommittedSequence
```

Use deletion markers when consumed messages should disappear from normal scans:

```text
queue:{queue}:{sequence} -> deleted marker
```

Use per-message state when multiple consumers or retry states must be tracked:

```text
queue-state:{queue}:{sequence}:{consumerGroup} -> state
```

Choose the model based on the delivery semantics of the system, not because one layout is universally correct.

## Visibility And Retry

Visibility timeouts are an application-level protocol. A typical design stores claim state separately from the payload:

```text
claim:{queue}:{sequence} -> consumerId, claimedUntilUtc, attempt
```

Consumers can scan queue payloads, check claim state, and claim expired messages with atomic operations or transactions. If the correctness rule touches several keys, use transactions or design the operation to be idempotent and repairable.

## Retention

Retention can be modeled with:

* committed consumer offsets,
* deletion markers for consumed messages,
* TTL-style values,
* partitioned queues by time bucket,
* rebuild or compaction jobs.

ZoneTree maintenance eventually removes obsolete records through merges. A delete is not an immediate physical file rewrite.

## Partitioning

For large queues, avoid making one key range carry all operational pressure.

Useful partition shapes:

```text
queue:{name}:{partition}:{sequence}
tenant:{tenantId}:queue:{name}:{sequence}
queue:{name}:{yyyyMMdd}:{sequence}
```

Partition when it gives you a real operational boundary: independent consumers, smaller restore windows, bounded retention, smaller scans, or better tenant isolation.

## Transactions

Use transactions when queue correctness spans multiple keys:

* append payload and secondary index together,
* append payload and global order entry together,
* update message state and consumer offset together,
* claim a message only if claim state is still expired.

Use regular writes when the operation is a simple append or offset update. Use atomic methods when one key, such as a counter or claim record, decides its own next value.

## Scope

ZoneTree provides the storage engine primitives:

* ordered durable keys,
* forward and reverse iterators,
* deletion markers,
* same-key atomic operations,
* transactions for multi-key coordination,
* maintenance and compaction.

The queue layer owns:

* delivery semantics,
* consumer groups,
* leases and visibility timeouts,
* retries and dead-letter queues,
* idempotency,
* distributed coordination,
* monitoring and backpressure.

That split is intentional. ZoneTree is the durable ordered foundation; the queue system defines the protocol.
