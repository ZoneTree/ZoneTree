# Reads And Writes

ZoneTree provides multiple write APIs because different operations need different coordination costs. Use the simplest API that matches the data rule you need.

## API Choice

| Need | Use |
| --- | --- |
| Set or replace a value | `Upsert` |
| Add only if the key does not exist | `TryAdd` |
| Delete a key if it exists | `TryDelete` |
| Write a deletion marker without checking existence | `ForceDelete` |
| Update based on the current value | atomic methods |
| Coordinate multiple keys | transactions |

## Upsert

`Upsert` is the normal high-throughput write path.

```csharp
var opIndex = zoneTree.Upsert(42, "value");
```

It adds the key if it does not exist or replaces the latest value if it does.

## TryGet

```csharp
if (zoneTree.TryGet(42, out var value))
{
    Console.WriteLine(value);
}
```

`TryGet` searches the newest layers first and ignores values considered deleted by the configured deletion logic.

Treat returned values as immutable snapshots. If `TValue` is a mutable reference type, do not mutate the returned object as a way to update the database. Create a new value and write it back.

See [value mutability](../concepts/value-mutability.md).

## TryAdd

```csharp
if (zoneTree.TryAdd(42, "value", out var opIndex))
{
    Console.WriteLine("added");
}
```

Use `TryAdd` when duplicate keys should not be overwritten.

## TryDelete And ForceDelete

```csharp
zoneTree.TryDelete(42, out var opIndex);
zoneTree.ForceDelete(42);
```

`TryDelete` checks whether the key exists. `ForceDelete` writes a deletion marker directly.

## Thread Safety

Regular write APIs are designed for normal concurrent use. Atomic methods are not the "safe version" of regular writes; they are the synchronized read-modify-write tools.

Use `Upsert` for simple concurrent writes. Use atomic methods only when the new value depends on the existing value.

## Mixed Write Modes

You can mix write modes in the same tree. For example:

* ordinary records use fast `Upsert`,
* counter keys use atomic read-modify-write,
* multi-key changes use transactions.

This lets each workflow pay only for the coordination it needs.

## Value Shape

Small immutable structs or readonly record structs are often a good fit for ZoneTree values. They reduce accidental shared mutation and can reduce GC pressure for in-memory segments.

For larger or complex values, use immutable reference types, records, serialized payloads, or clone-and-upsert patterns.
