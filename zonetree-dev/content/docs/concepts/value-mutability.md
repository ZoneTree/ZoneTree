# Value Mutability

Values stored in ZoneTree should be treated as snapshots.

This matters when `TValue` is a mutable reference type. While a value is still in an in-memory segment, ZoneTree may hold the same object reference the application inserted. Mutating that object outside a ZoneTree write can change the visible value without a WAL record, without a new operation index, and without a durable update.

## Shared Reference Hazard

```csharp
public sealed class User
{
    public string Name { get; set; }
}

var user = new User { Name = "Alice" };

zoneTree.Upsert(1, user);

user.Name = "Bob"; // mutates an object that may already be stored by reference
```

A value returned by `TryGet` should also be treated as a snapshot:

```csharp
if (zoneTree.TryGet(1, out var user))
{
    user.Name = "Bob"; // no ZoneTree write happens here
}
```

The behavior can differ depending on whether the value came from the mutable segment, a read-only segment, or disk deserialization. That difference makes shared mutation hard to reason about.

## Update Discipline

Create the next value and write it through ZoneTree:

```csharp
public sealed record UserSnapshot(string Name);

if (zoneTree.TryGet(1, out var user))
{
    var updated = user with { Name = "Bob" };
    zoneTree.Upsert(1, updated);
}
```

For mutable classes, clone before changing the value:

```csharp
if (zoneTree.TryGet(1, out var user))
{
    var updated = user.Clone();
    updated.Name = "Bob";
    zoneTree.Upsert(1, updated);
}
```

## Value Shapes

Small record-like payloads are often a good fit for structs or readonly record structs:

```csharp
public readonly record struct CounterValue(long Count);

public readonly record struct UserScore(int Score, long UpdatedAt);

public readonly record struct QueuePointer(long Sequence);
```

These shapes give value semantics, reduce accidental shared mutation, reduce GC pressure in in-memory segments, and serialize compactly when paired with appropriate serializers.

Reference types are still valid for large or complex values. Prefer immutable classes, records, serialized payloads, or clone-and-upsert patterns.

## Atomic Delegates

Atomic update delegates receive a local `TValue` variable by `ref`. They are the controlled place where a value can be transformed as part of a ZoneTree write.

For value types, this is direct. For mutable reference types, assigning a new object to the `ref` parameter is the safest pattern. In-place mutation can be acceptable when the change is idempotent, repeatable, and never cancelled after mutation.

Some atomic methods can retry before they finish. If a delegate mutates an existing reference-type value in place, the shared object changes immediately, before ZoneTree has necessarily accepted the write, assigned an operation index, and appended the WAL record.

The cancellation case is the trap. Returning `false` tells ZoneTree not to write the local value back. If the delegate mutates a shared reference object before returning `false`, the object may already have changed in memory.

Decide first, then mutate or assign:

```csharp
zoneTree.TryAtomicGetAndUpdate(1, out var user, (ref UserSnapshot value) =>
{
    if (!ShouldRename(value))
        return false;

    value = value with { Name = "Bob" };
    return true;
});
```

Returning `false` should mean the delegate made no observable change.
