# Atomic Operations

Atomic methods are ZoneTree's single-key read-modify-write tools.

Use them when the next value depends on the value that is already visible for the same key:

| Need | Use |
| --- | --- |
| Increment a counter | atomic method |
| Append to the current value | atomic method |
| Compare current value before replacing it | atomic method |
| Initialize if absent, update if present | `TryAtomicAddOrUpdate` |
| Simple insert or replace | `Upsert` |
| Several keys must change together | transaction |

`Upsert`, `TryAdd`, `TryDelete`, and `ForceDelete` are the normal high-throughput write APIs. They are thread-safe, but they do not join the atomic-method lock. If a key's correctness depends on read-modify-write behavior, keep every write for that key on the atomic path.

## What Atomic Means

ZoneTree is an LSM-tree. The newest value for a key may be in the mutable segment, a read-only in-memory segment, the disk segment, or a bottom segment until maintenance merges older layers away.

Atomic methods synchronize this sequence:

1. Read the current visible value across the LSM-tree layers.
2. Decide whether to add, update, cancel, or replace.
3. Write the new value to the mutable segment.

Atomic methods are ordered with other atomic methods on the same tree. Regular writes do not participate in that ordering. That is why `AtomicUpsert` exists: it is the upsert form to use when a key also participates in atomic read-modify-write operations.

## Counter

```csharp
const string key = "stats:user:42:views";

zoneTree.TryAtomicAddOrUpdate(
    key,
    valueToAdd: 1,
    valueUpdater: (ref long value) =>
    {
        value++;
        return true;
    });
```

If the key is absent, ZoneTree writes `1`. If it is present, the updater receives the current value and writes back the incremented value.

## Compare And Set

```csharp
var found = zoneTree.TryAtomicGetAndUpdate(
    "order:123:state",
    out var current,
    (ref OrderState state) =>
    {
        if (state != OrderState.Pending)
            return false;

        state = OrderState.Confirmed;
        return true;
    });
```

`found` is `false` only when the key is not found. If the key is found and the delegate returns `false`, the method still returns `true` because the read succeeded, but no new value is written.

## Initialize Or Update With A Callback

```csharp
zoneTree.TryAtomicAddOrUpdate(
    key: "counter:global",
    valueToAdd: 1,
    valueUpdater: (ref long value) =>
    {
        value++;
        return true;
    },
    result: (in long value, long opIndex, OperationResult result) =>
    {
        Console.WriteLine($"{result}: {value} at {opIndex}");
    });
```

The result callback reports:

| Result | Meaning |
| --- | --- |
| `Added` | The key was absent and a new value was written. |
| `Updated` | The key existed and the updated value was written. |
| `Cancelled` | The adder or updater returned `false`; no value was written. |

When an operation is cancelled, the callback receives `opIndex` `0`.

`TryAtomicAddOrUpdate` returns `true` when it adds a new key and `false` when it updates or cancels. Use the result callback when the caller needs to distinguish `Updated` from `Cancelled`.

## Method Guide

| Method | Behavior |
| --- | --- |
| `TryAtomicAdd(key, value, out opIndex)` | Adds only when the key is absent. Returns `false` and `opIndex = 0` when the key already exists. |
| `TryAtomicUpdate(key, value, out opIndex)` | Replaces only when the key exists. Returns `false` and `opIndex = 0` when the key is absent. |
| `AtomicUpsert(key, value)` | Adds or replaces under the atomic-method lock and returns the operation index. |
| `TryAtomicGetAndUpdate(key, out value, updater)` | Reads the current value and lets the updater decide whether to write a replacement. Returns `false` when the key is absent. |
| `TryAtomicAddOrUpdate(key, valueToAdd, updater, result)` | Adds `valueToAdd` when absent; otherwise updates the current value. |
| `TryAtomicAddOrUpdate(key, adder, updater, result)` | Lets the adder create the absent value and the updater change the existing value. Either delegate can cancel by returning `false`. |

All successful writes are assigned a normal operation index. Operation indexes preserve ZoneTree's producer write order and are useful for replay, replication, audit, and restore workflows.

## Delegate Rules

`ValueUpdaterDelegate<TValue>` and `ValueAdderDelegate<TValue>` receive a local `TValue` variable by `ref`.

```csharp
public delegate bool ValueUpdaterDelegate<TValue>(ref TValue value);
public delegate bool ValueAdderDelegate<TValue>(ref TValue value);
```

Return `true` to commit the local value. Return `false` to cancel the write.

Keep delegates and result callbacks short and deterministic. `TryAtomicAddOrUpdate` may retry when the mutable segment is frozen or full, so adder/updater delegates can be invoked more than once before the method finishes. Avoid external side effects inside those delegates unless repeating the side effect is acceptable.

For mutable reference types, assigning a new object to the `ref` parameter is the safest pattern. In-place mutation can be acceptable when the change is idempotent, repeatable, and never cancelled after mutation. It still changes the shared object immediately, before ZoneTree has necessarily accepted the write, assigned an operation index, and appended the WAL record.

Returning `false` prevents ZoneTree from writing a new value, but it cannot undo in-place changes already made to a shared object reference.

```csharp
zoneTree.TryAtomicGetAndUpdate(1, out var user, (ref UserSnapshot value) =>
{
    if (!ShouldRename(value))
        return false;

    value = value with { Name = "Bob" };
    return true;
});
```

See [value mutability](../concepts/value-mutability.md).

## Mixing Write Modes

Mixing write modes is fine when they protect different keys or different invariants:

* `stats:user:123` uses atomic increments,
* `profile:user:123` uses regular `Upsert`,
* secondary index entries use regular `Upsert` or `ForceDelete`.

Do not mix regular writes and atomic writes for the same read-modify-write invariant:

```csharp
// Avoid this shape for the same counter key.
zoneTree.TryAtomicAddOrUpdate("counter", 1, (ref long value) =>
{
    value++;
    return true;
});

zoneTree.Upsert("counter", 0);
```

The `Upsert` is thread-safe, but it is not synchronized with the atomic read-decision-write sequence. If the key belongs to an atomic workflow, use `AtomicUpsert` for unconditional replacement.

## When To Use Transactions

Atomic methods coordinate one key's current value. Use transactions when a correctness rule spans multiple keys:

* transfer from one account key to another,
* append an event and update a separate stream pointer,
* update a record and several secondary indexes as one unit,
* reserve a global sequence number and write the payload in the same commit.

For one-key counters, compare-and-set logic, and initialize-or-update behavior, atomic methods are the smaller and faster tool.
