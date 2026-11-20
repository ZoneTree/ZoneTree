# ZoneTree.Composite

`ZoneTree.Composite` stores several values in one `byte[]`, with no serializer, comparer, or hasher to
write:

- **`CompositeKey`** builds keys that sort by their values, element by element. Use it for rows, index
  entries, and anything you scan by range.
- **`CompositeValue`** builds values whose elements are read one at a time. Use it for rows of several
  fields.

Both produce plain byte arrays, so a `ZoneTreeFactory<byte[], byte[]>` stores them as they are. Every
element records its type, so any code can read a key or value back without knowing what wrote it.

## Quick start

Orders keyed by customer and order id: a customer's orders sort together, so one scan reads them all.

```csharp
using ZoneTree;
using ZoneTree.Composite;

using var tree = new ZoneTreeFactory<byte[], byte[]>()
    .SetDataDirectory("data/orders")
    .OpenOrCreate();
using var maintainer = tree.CreateMaintainer();

// Keys: (customer, orderId). Values: (total).
tree.Upsert(CompositeKey.Create("bob", 1L), CompositeValue.Create(19.99m));
tree.Upsert(CompositeKey.Create("alice", 2L), CompositeValue.Create(5.00m));
tree.Upsert(CompositeKey.Create("bob", 3L), CompositeValue.Create(7.50m));

// Every order of bob: the keys that start with "bob".
var bob = CompositeKey.Range(CompositeKey.Create("bob"));
using var iterator = tree.CreateIterator();
iterator.Seek(bob.Lower);
while (iterator.Next() && bob.Contains(iterator.CurrentKey))
{
    var id = CompositeKey.Get<long>(iterator.CurrentKey, 1);
    var total = CompositeValue.Get<decimal>(iterator.CurrentValue, 0);
    Console.WriteLine($"order {id}: {total}");
}
// order 1: 19.99
// order 3: 7.50

// One order, by its whole key.
if (tree.TryGet(CompositeKey.Create("bob", 3L), out var value))
    Console.WriteLine(CompositeValue.Get<decimal>(value, 0));  // 7.50
```

`Range` holds the keys that start with the given elements, and the scan reads them in key order.

## Composite keys

### Create and read a key

```csharp
var key = CompositeKey.Create("by-customer", "bob", 42L);

var (index, customer, id) = CompositeKey.Decode<string, string, long>(key);  // every element
var (_, name) = CompositeKey.Decode<string, string>(key);                   // the first two
var orderId = CompositeKey.Get<long>(key, 2);                              // one, by position
var small = CompositeKey.Get<int>(key, 2);                                 // 42 fits an int too
```

`Create` and `Decode` take up to eight elements. An integer reads as any integer type that holds it, so a
key written with a `long` reads as an `int`, and one written with an `int` reads as a `long`.

### Rows and indexes

To find orders both by id and by customer, store each order under two keys: a row keyed by its id, and an
index entry keyed by its customer and id. The application writes both; nothing updates an index by itself.
The keys can live in separate trees, or in one tree with each kind of key under a leading element of its own:

```csharp
// In separate trees, each holding one kind of key.
rows.Upsert(CompositeKey.Create(id), CompositeValue.Create(customer, total));
byCustomer.Upsert(CompositeKey.Create(customer, id), CompositeValue.Create(total));

// In one tree, where "row" and "by-customer" keep each kind of key in a range of its own.
tree.Upsert(CompositeKey.Create("row", id), CompositeValue.Create(customer, total));
tree.Upsert(CompositeKey.Create("by-customer", customer, id), CompositeValue.Create(total));
```

A transaction covers the keys of one transactional tree, so only keys in one tree commit together. The
samples below keep their keys in one tree.

### Scan a range

A range is a `CompositeKeyRange` of two bounds: `Lower`, where a forward scan starts, and `Upper`, where a
reverse scan starts. A scan seeks its bound and reads while the range `Contains` the key. The samples below
read ranges with this helper:

```csharp
static IEnumerable<(byte[] Key, byte[] Value)> Scan(
    IZoneTree<byte[], byte[]> tree, CompositeKeyRange range, bool reverse = false)
{
    using var iterator = reverse ? tree.CreateReverseIterator() : tree.CreateIterator();
    iterator.Seek(reverse ? range.Upper : range.Lower);
    while (iterator.Next() && range.Contains(iterator.CurrentKey))
        yield return (iterator.CurrentKey, iterator.CurrentValue);
}
```

A range keeps the arrays of its bounds, so don't change them.

These functions make ranges:

| Function | The keys it holds |
| --- | --- |
| `Range(prefix)` | Those that start with the prefix's elements |
| `Between(low, high, bounds)` | Those from one prefix through another |
| `AtLeast(low)`, `GreaterThan(low)`, `AtMost(high)`, `LessThan(high)` | Those on one side of a value, after the same leading elements |
| `BeginsWith(prefix, text)` | Those whose next element is a string that begins with the text |

**`Range`** finds the keys under some leading elements:

```csharp
foreach (var (key, value) in Scan(tree, CompositeKey.Range(CompositeKey.Create("by-customer", "bob"))))
    Console.WriteLine(CompositeKey.Get<long>(key, 2));  // every order id of bob
```

It matches whole elements: the prefix `("by-customer", "bob")` holds `("by-customer", "bob", 7)`, but not
`("by-customer", "bobby", 7)`.

**`Between`** finds the keys from one value through another, here orders keyed by the day they were placed:

```csharp
// Keys: ("by-day", day, orderId)
var monday = new DateOnly(2026, 11, 9);
var friday = monday.AddDays(4);

var week = CompositeKey.Between(CompositeKey.Create("by-day", monday), CompositeKey.Create("by-day", friday));
foreach (var (key, _) in Scan(tree, week))
{
    var (_, day, id) = CompositeKey.Decode<string, DateOnly, long>(key);
    Console.WriteLine($"{day}: order {id}");  // Monday through Friday
}
```

Both values are included unless a third argument, `CompositeKeyBounds`, excludes one:

| `CompositeKeyBounds` | Holds |
| --- | --- |
| `Inclusive`, the default | Both values |
| `ExcludeLow` | The high value, not the low one |
| `ExcludeHigh` | The low value, not the high one: from a start up to an end |
| `Exclusive` | Only what lies between the two |

```csharp
var workdays = CompositeKey.Between(
    CompositeKey.Create("by-day", monday),
    CompositeKey.Create("by-day", monday.AddDays(5)),  // Saturday
    CompositeKeyBounds.ExcludeHigh);                   // Monday up to, not including, Saturday

var ordersThisWeek = Scan(tree, workdays).Count();
```

**`AtLeast(low)`, `GreaterThan(low)`, `AtMost(high)`, and `LessThan(high)`** take one end, as `Between` does:
a prefix whose last element is the value, after elements that stay fixed:

```csharp
var fromFriday = CompositeKey.AtLeast(CompositeKey.Create("by-day", friday));      // Friday and later
var beforeMonday = CompositeKey.LessThan(CompositeKey.Create("by-day", monday));   // every day before Monday

foreach (var (key, _) in Scan(tree, fromFriday))
    Console.WriteLine($"order {CompositeKey.Get<long>(key, 2)}");
```

**`BeginsWith`** finds the strings that begin with some text, as a search box does while the user types:

```csharp
// Keys: ("by-name", name, customerId)
foreach (var (key, _) in Scan(tree, CompositeKey.BeginsWith(CompositeKey.Create("by-name"), "bo")))
    Console.WriteLine(CompositeKey.Get<string>(key, 1));  // bo, bob, bobby: not bp or alice
```

Its other overload takes bytes, for prefixes of hashes or paths.

### Scan backward

`reverse: true` reads a range from its last key to its first:

```csharp
var bob = CompositeKey.Range(CompositeKey.Create("by-customer", "bob"));
foreach (var (key, _) in Scan(tree, bob, reverse: true))
    Console.WriteLine(CompositeKey.Get<long>(key, 2));  // bob's order ids, highest first
```

On a tree created with `BytesLexicographicComparerDescending`, each iterator walks the other way, so swap
`Lower` and `Upper` in the helper.

### Read page by page

`StartAfter` continues a range above the highest key a page read, and `EndBefore` below the lowest one. The key
is all a caller keeps between pages:

```csharp
var bob = CompositeKey.Range(CompositeKey.Create("by-customer", "bob"));

var first = Scan(tree, bob).Take(20).ToList();
if (first.Count > 0)
{
    var second = Scan(tree, bob.StartAfter(first[^1].Key)).Take(20).ToList();  // the next 20
}

var last = Scan(tree, bob, reverse: true).Take(20).ToList();
if (last.Count > 0)
{
    var earlier = Scan(tree, bob.EndBefore(last[^1].Key), reverse: true).Take(20).ToList();  // the 20 before them
}
```

`StartAfter` and `EndBefore` take one exact key, as a cursor does, so the next page starts right next to it and
skips no key. `GreaterThan` and `LessThan` take an end instead, which stands for every key under it.

On a tree created with `BytesLexicographicComparerDescending`, each iterator walks the other way, so a forward
page continues with `EndBefore` and a backward page with `StartAfter`.

### Sort an element newest first

`CompositeKey.Descending(value)` makes one element sort in reverse. The other elements keep their own
order, and the element reads back as its own type:

```csharp
// For each order: a key of the time it was placed, newest first, then its id.
tree.Upsert(CompositeKey.Create("by-time", CompositeKey.Descending(placed), id), CompositeValue.Create(total));

foreach (var (key, _) in Scan(tree, CompositeKey.Range(CompositeKey.Create("by-time"))))
{
    var placedAt = CompositeKey.Get<DateTime>(key, 1);  // a DateTime, not a Descending<DateTime>
    Console.WriteLine($"{placedAt}: order {CompositeKey.Get<long>(key, 2)}");  // newest first
}
```

`Between` takes its values in either order, so the same call works on a descending element.
`CompositeKeyBounds` excludes a value by its argument: `ExcludeHigh` always excludes `high`, even where
`high` sorts first. A reverse scan reverses whole keys instead.

### Fix a key space with a layout

A layout is a definition of one kind of key: the elements every key starts with, then the type and the
order of each position. Every key made through it has the same shape, so a range of the layout holds
exactly its keys, and the compiler checks every call:

```csharp
static class Orders
{
    public static readonly CompositeKeyLayout<long> Rows = new(CompositeKey.Create("row"));
    public static readonly CompositeKeyLayout<string, long> ByCustomer = new(CompositeKey.Create("by-customer"));
    public static readonly CompositeKeyLayout<DateTime, long> ByTime =
        new(CompositeKey.Create("by-time"), CompositeKeyOrder.Descending);  // newest first
}

void PlaceOrder(long id, string customer, DateTime placed, decimal total)
{
    tree.Upsert(Orders.Rows.Create(id), CompositeValue.Create(customer, placed, total));
    tree.Upsert(Orders.ByCustomer.Create(customer, id), CompositeValue.Create(total));
    tree.Upsert(Orders.ByTime.Create(placed, id), CompositeValue.Create(total));
}

// Bob's orders 10 through 20.
foreach (var (key, value) in Scan(tree, Orders.ByCustomer.Between("bob", 10L, 20L)))
{
    var (customer, id) = Orders.ByCustomer.Decode(key);
    Console.WriteLine($"{id}: {CompositeValue.Get<decimal>(value, 0)}");
}

// Today's orders, newest first: from midnight up to the next midnight.
var today = DateTime.UtcNow.Date;
foreach (var (key, _) in Scan(tree, Orders.ByTime.Between(today, today.AddDays(1), CompositeKeyBounds.ExcludeHigh)))
{
    var (placed, id) = Orders.ByTime.Decode(key);
    Console.WriteLine($"{placed:t} order {id}");
}

// How many orders were placed in the last seven days: at or after midnight a week ago.
var lastWeek = Scan(tree, Orders.ByTime.AtLeast(today.AddDays(-7))).Count();

// Bob's orders after order 20.
foreach (var (key, _) in Scan(tree, Orders.ByCustomer.GreaterThan("bob", 20L)))
    Console.WriteLine(Orders.ByCustomer.Decode(key).Item2);
```

| Member | Does |
| --- | --- |
| `Create(...)` | Makes a key of the layout |
| `Decode(key)`, `TryDecode(key, out ...)` | Reads a key, checking its prefix, the order of each element, and that nothing follows |
| `Range()` | Makes the range of every key of the layout |
| `Range(first, ...)` | Makes the range of the keys that start with some elements |
| `Between(low, high, bounds)`, `Between(first, ..., low, high, bounds)` | Makes the range of the keys from one value through another at one position, after fixed elements before it |
| `AtLeast(first, ..., low)`, `GreaterThan(first, ..., low)`, `AtMost(first, ..., high)`, `LessThan(first, ..., high)` | Makes the range of the keys on one side of a value at one position, after fixed elements before it |
| `BeginsWith(first, ..., text)`, `BeginsWith(first, ..., bytes)` | Makes the range of the keys whose string or bytes at one position begins with the text or bytes |
| `Matches(key)` | Tells whether a key starts with the layout's prefix |
| `Prefix`, `ElementCount`, `Order(position)` | What the layout fixed |

The position of the last argument is the one a range limits; the arguments before it are the elements before
that position. Every range compares by value, so on the newest-first position of `ByTime`, `AtLeast` holds the
newer orders.

A layout has a typed form for one to eight elements. It checks its types when it is made, so an unsupported
type fails at startup rather than at the first write. It is safe for concurrent use, and making a key through
it costs what `CompositeKey.Create` costs.

`BeginsWith` takes a string or bytes position, in the layout's order for it:

```csharp
static readonly CompositeKeyLayout<string, long> ByName = new(CompositeKey.Create("by-name"));

foreach (var (key, _) in Scan(tree, ByName.BeginsWith("bo")))
{
    var (name, customerId) = ByName.Decode(key);
    Console.WriteLine($"{name}: customer {customerId}");
}
```

At a position of another type, it throws `PositionTypeMismatchException`, which gives the position, its type,
and the element type the range needs.

### Build long keys

`CompositeKeyBuilder` builds keys of any length. Here a file's key is the segments of its path, so `Range`
over a folder holds everything inside it, at any depth:

```csharp
static byte[] FileKey(string path)
{
    var builder = new CompositeKeyBuilder().Add("files");
    foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        builder.Add(segment);
    return builder.ToArray();
}

tree.Upsert(FileKey("/usr/local/bin/zonetree"), CompositeValue.Create(4096L));
tree.Upsert(FileKey("/usr/localhost/readme"), CompositeValue.Create(12L));

foreach (var (key, value) in Scan(tree, CompositeKey.Range(FileKey("/usr/local"))))
    Console.WriteLine(CompositeKey.Format(key));  // ("files", "usr", "local", "bin", "zonetree") only
```

`Range` matches whole segments, so `/usr/localhost` is not inside `/usr/local`. A builder keeps its buffer
across `Clear()`, so one builder reused for many keys allocates only the arrays `ToArray()` returns.

### Join and slice keys

A key is its elements one after another, with no header. Two keys joined end to end are the key of all their
elements, and `ElementBytes` slices elements out of a key as a key of their own. An index entry can lead
back to its row without decoding anything:

```csharp
var entry = CompositeKey.Create("by-customer", "bob", 42L);

// The row's key is "row" followed by the entry's last element, the order id.
byte[] rowKey = [.. CompositeKey.Create("row"), .. CompositeKey.ElementBytes(entry, 2, 1)];
if (tree.TryGet(rowKey, out var row))
    Console.WriteLine(CompositeValue.Format(row));
```

`CompositeKeyBuilder.AddElements` appends encoded elements the same way.

### Inspect a key

`CompositeKeyReader` walks a key's elements in one pass, without allocating:

```csharp
var reader = new CompositeKeyReader(key);
while (reader.Next())
    Console.WriteLine($"{reader.Index}: {reader.ElementType}{(reader.IsDescending ? ", descending" : "")}");
```

| Member | Gives |
| --- | --- |
| `ElementCount(key)` | The number of elements |
| `ElementType(key, index)`, `IsDescending(key, index)` | One element's type and order |
| `ElementBytes(key, index)`, `ElementBytes(key, start, count)` | The encoded bytes of one element, or of a run of them |
| `Offsets(key, offsets)` | Where every element starts, found in one pass |
| `IsValid(key)` | Whether the bytes are a well-formed composite key |
| `Format(key)` | The key as text; see [Keys and values as text](#keys-and-values-as-text) |

`Get(key, i)` reads past the elements before `i`, which costs little for short keys. To read many elements of
one long key, walk it once with the reader or `Offsets`.

### Compare elements without decoding

Every value has one encoding, so an element equals a value exactly when their bytes are equal. Encoding the
value once and comparing bytes tests an element of any type, with no decoding and no allocation:

```csharp
// Keys: ("sales", day, customer, orderId)
var week = CompositeKey.Between(CompositeKey.Create("sales", monday), CompositeKey.Create("sales", friday));
var bob = CompositeKey.Create("bob");  // the element's bytes, encoded once

foreach (var (key, _) in Scan(tree, week))
{
    if (CompositeKey.ElementBytes(key, 2).SequenceEqual(bob))
        Console.WriteLine($"order {CompositeKey.Get<long>(key, 3)}");  // bob's orders of the week
}
```

A descending element compares with a value encoded descending, as `CompositeKey.Create(CompositeKey.Descending("bob"))`.

### Compare keys and text in memory

A key is a byte array, and `==` on arrays compares references, not bytes. For sets, dictionaries, and
sorts of keys, use the key comparer that matches the tree's comparer:

| Tree's comparer | Key comparer |
| --- | --- |
| `BytesLexicographicComparerAscending`, the default for `byte[]` keys | `CompositeKey.ComparerAscending` |
| `BytesLexicographicComparerDescending` | `CompositeKey.ComparerDescending` |

Both compare bytes, so they find the same keys equal; only their order differs.

```csharp
var seen = new HashSet<byte[]>(CompositeKey.ComparerAscending);
var sorted = keys.Order(CompositeKey.ComparerAscending);
```

Keys sort strings by their Unicode code points. .NET's ordinal comparison, `string.CompareOrdinal` and
`StringComparer.Ordinal`, sorts UTF-16 code units instead, and the two orders disagree where a character above
U+FFFF, such as an emoji, meets one from U+E000 to U+FFFF. To sort or merge strings in the order a forward
scan returns them, use the text comparer that matches the string element and the tree's comparer:

| String element | Tree's comparer | Text comparer |
| --- | --- | --- |
| Ascending | `BytesLexicographicComparerAscending` | `CompositeKey.TextComparerAscending` |
| Descending | `BytesLexicographicComparerAscending` | `CompositeKey.TextComparerDescending` |
| Ascending | `BytesLexicographicComparerDescending` | `CompositeKey.TextComparerDescending` |
| Descending | `BytesLexicographicComparerDescending` | `CompositeKey.TextComparerAscending` |

```csharp
var names = customers.Order(CompositeKey.TextComparerAscending);
```

A null string sorts where a null element does: first in ascending order, and last in descending order.

## Composite values

### Create and read a value

```csharp
var row = CompositeValue.Create("bob", placed, 19.99m);

var (customer, when, total) = CompositeValue.Decode<string, DateTime, decimal>(row);  // every element
var totalOnly = CompositeValue.Get<decimal>(row, 2);                                 // only the total

// A string's UTF-8 bytes, read in place: no string is created.
bool isBob = CompositeValue.ElementPayload(row, 0).SequenceEqual("bob"u8);
```

`Get` reads one element without decoding the others, and strings and bytes are stored as they are. Every
element reads back exactly as written: a decimal keeps its scale (`7.50m`, not `7.5m`), a `DateTime` its kind,
a `DateTimeOffset` its offset, and a `float` its size.

A tree of `byte[]` values takes an empty array for a deletion. A composite value is never empty, even with no
elements, so it is a safe value for an index entry too.

### Change one element

`With` replaces one element, and `Append` adds one at the end. Both copy the other elements as bytes, without
decoding them, into one new array. Under a key lock, changing one field loses no other change to the row, as
long as every writer of the row takes the lock:

```csharp
var key = Orders.Rows.Create(42);
using (tree.Lock(key))
{
    var row = tree.TryGet(key, out var stored)
        ? CompositeValue.With(stored, 2, 15.99m)          // only the total changes
        : CompositeValue.Create("bob", placed, 15.99m);  // added if the order is missing
    tree.Upsert(key, row);
}
```

### Let rows grow

`Decode` reads leading elements and ignores the rest, so new fields go at the end and old rows stay
readable. `TryGet` tells a row that has a field from one that does not:

```csharp
// Rows written before coupons existed hold (customer, placed, total); newer ones add a coupon.
var (customer, placed, total) = CompositeValue.Decode<string, DateTime, decimal>(row);
var coupon = CompositeValue.TryGet(row, 3, out string code) ? code : "none";

// Give an old row its coupon: the other elements are copied as they are.
// The lock keeps the read of the current row and the write together.
using (tree.Lock(key))
{
    if (tree.TryGet(key, out var current) && !CompositeValue.TryGet(current, 3, out string _))
        tree.Upsert(key, CompositeValue.Append(current, "WELCOME10"));
}
```

### Build and project values

`CompositeValueBuilder` builds values of any length. Its `AddElements` copies elements of another value as
they are, which is how a summary keeps some fields of a row:

```csharp
var summary = new CompositeValueBuilder()
    .AddElements(row, start: 0, count: 1)  // the customer, copied without decoding
    .Add(itemCount)
    .ToArray();
tree.Upsert(CompositeKey.Create("summary", id), summary);
```

`CompositeValueReader` walks a value's elements in one pass:

```csharp
var reader = new CompositeValueReader(row);
while (reader.Next())
    Console.WriteLine($"{reader.Index}: {reader.ElementType}, {reader.Payload.Length} bytes");
```

## Reading without exceptions

Every member that reads elements as types has a Try form. It returns false where the plain form throws: a
missing element, another type, a value the type cannot hold, malformed bytes, or a string of invalid UTF-8.
A failed read leaves the default value.

| Reads | Try form |
| --- | --- |
| `CompositeKey.Get`, `CompositeValue.Get` | `TryGet(bytes, index, out value)` |
| `CompositeKey.Decode`, `CompositeValue.Decode` | `TryDecode(bytes, out first)` or `TryDecode(bytes, out (T1, T2, ...) elements)` |
| A layout's `Decode` | `TryDecode(key, out elements)`, false for a key of another layout too |
| A reader's `Read<T>()` | `TryRead(out value)` |

A scan over a whole tree reads each key as the kind it is:

```csharp
using var iterator = tree.CreateIterator();
while (iterator.Next())
{
    var key = iterator.CurrentKey;
    if (Orders.Rows.TryDecode(key, out var id))
        Console.WriteLine($"order {id}");
    else if (Orders.ByCustomer.TryDecode(key, out (string Customer, long Id) entry))
        Console.WriteLine($"{entry.Customer}'s order {entry.Id}");
    else
        Console.WriteLine($"something else: {CompositeKey.Format(key)}");
}
```

The Try forms throw `NotSupportedException` for a type no element can be, such as `object`: that is a mistake
in the code, not in the bytes.

## Keys and values as text

`CompositeKey.Format` and `CompositeValue.Format` turn any bytes into readable text, for logs, debuggers, and
tools. They never throw, and the text is the same in every culture:

```csharp
tree.Upsert(key, value);
Console.WriteLine($"wrote {CompositeKey.Format(key)} = {CompositeValue.Format(value)}");
// wrote ("by-time", desc(2026-11-15T09:30:00.0000000), 42) = ("bob", 19.99m)
```

| Element | Text |
| --- | --- |
| Null | `null` |
| Bytes | `0x00AB` |
| String | `"say \"hi\""`, with the escapes of C# |
| Integer | `42` |
| Floating | `2.0`, `1.5`, `NaN`: always with a point or an exponent |
| Decimal | `19.99m` |
| Boolean | `true` |
| `DateTime` | `2026-11-15T09:30:00.0000000`; in a value, with `Z` for UTC or ` local` for local time |
| `DateTimeOffset` | `2026-11-15T09:30:00.0000000+03:00` |
| `DateOnly` | `2026-11-15` |
| `TimeOnly` | `T09:30:00.0000000` |
| `TimeSpan` | `-01:30:00`, `1.02:03:04.5000000` |
| `Guid` | `0f8fad5b-d9cb-469f-a165-70867728950e` |
| A descending key element | `desc(...)` |
| Bytes that are not elements | `malformed(0x...)` |

In a key, the bytes after the point where it stops being a key show as one `malformed(...)`. In a value,
each malformed element shows its own payload, and a value with a malformed header shows all its bytes.

## Reference

### Element types

| .NET type | Element type | Notes |
| --- | --- | --- |
| `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `nint`, `nuint`, `Int128`, `UInt128`, `BigInteger`, `char`, enums | Integer | Compared and read by value; an enum is its underlying value |
| `Half`, `float`, `double` | Floating | |
| `decimal` | Decimal | |
| `bool` | Boolean | |
| `DateTime` | DateTime | |
| `DateTimeOffset` | DateTimeOffset | |
| `DateOnly` | DateOnly | |
| `TimeOnly` | TimeOnly | |
| `TimeSpan` | TimeSpan | |
| `Guid` | Guid | |
| `string` | String | Must be valid UTF-16 |
| `byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>` | Bytes | |
| A null string or byte array, or a nullable value without a value | Null | Every value type above can be nullable |
| `Descending<T>` of a type above | The type's own | Keys only |

An element's type is the type argument of `Create`, `Add`, or `Get`, so an element passed as `object` is not
supported. `Int128` and `UInt128` need .NET 7 or later.

### How keys sort

Composite keys need a tree whose comparer is `BytesLexicographicComparerAscending`, the default for `byte[]`
keys, or `BytesLexicographicComparerDescending`. Ranges, `BeginsWith`, paging, and the comparers all work in
byte order, so with any other comparer they give wrong results.

- Elements compare one at a time, and a key sorts before every key that extends it.
- Elements of different types sort by type: null, bytes, string, integer, floating, decimal, boolean,
  `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid`, the order of
  `CompositeElementType`.
- At one position, ascending elements sort before descending ones.
- Within a type, elements sort by value, and each value has one encoding:
  - Integers compare by value whatever type wrote them: `5`, `5L`, and `(byte)5` are one key. An integer
    holds at most 255 bytes.
  - `0.0` and `-0.0` are one key, and every NaN is one key, sorting after positive infinity.
  - `1.0m` and `1.00m` are one key, which reads back as `1`.
  - A `DateTime` keeps only its ticks, and reads back with an unspecified kind.
  - A `DateTimeOffset` keeps only its UTC instant, and reads back with a zero offset.
  - Strings sort by Unicode code point, bytes lexicographically, and Guids as their text forms.

**Give each position one type and one order.** The same value written as an integer and as a string, or
ascending and descending, becomes two different keys, which sort apart. A layout makes this hold by
construction.

### Reading rules

An element reads as a type that holds its value. An integer reads as any integer type or enum that holds
it, a floating value as any floating type that holds it exactly, and a null as null for strings, byte arrays,
and nullable types. Every other element reads only as its own type.

| Exception | When |
| --- | --- |
| `InvalidCastException` | The element has another type |
| `OverflowException` | The type cannot hold the element's value exactly |
| `ArgumentOutOfRangeException` | No element is at the index, or a key's integer is wider than 255 bytes |
| `ArgumentException` | The bytes are malformed, there are fewer elements than `Decode` reads, or a string is not valid UTF-16 or UTF-8 |
| `NotSupportedException` | The type is not supported, or a value is given a descending element |

The Try forms return false instead, for every exception here but `NotSupportedException`.

### Concurrency and allocation

`CompositeKey`, `CompositeValue`, and layouts are safe for concurrent use. Builders are not, and readers are
stack-only structs, local to the method that creates them. `Create` allocates one array, of exactly the length
the key or value needs.

## Formats

The formats are fixed, so any language or tool can read and write them.

### Key format

A key is its elements one after another. Each element is a tag byte and a payload. A descending element is
its ascending bytes complemented, tag included, so descending tags run from `0x80` to `0xFE`. No element
starts with `0x00` or `0xFF`, so `0xFF` after a prefix comes after every key that starts with it.

| Tag | Element | Payload |
| --- | --- | --- |
| `0x01` | Null | None |
| `0x02` | Bytes | The bytes, each `0x00` written as `00 FF`, then `00 01` |
| `0x03` | String | The UTF-8 bytes, escaped and ended as bytes are |
| `0x04` | Negative integer of 9 to 255 bytes | The length complemented, then the magnitude complemented, big-endian |
| `0x05`–`0x0C` | Negative integer of 8 down to 1 bytes | The magnitude complemented, big-endian |
| `0x0D` | Zero | None |
| `0x0E`–`0x15` | Positive integer of 1 to 8 bytes | The value, big-endian |
| `0x16` | Positive integer of 9 to 255 bytes | The length, then the value, big-endian |
| `0x17` | Floating | The double's 8 bytes, big-endian: a positive value's sign bit flipped, a negative value's bits all flipped |
| `0x18`, `0x19`, `0x1A` | Negative decimal, zero, positive decimal | Nonzero: the exponent plus `0x80`, the significant digits as ASCII `'0'`–`'9'`, then `0x00`, all complemented for a negative decimal |
| `0x1B`, `0x1C` | False, true | None |
| `0x1D` | DateTime | Ticks, 8 bytes, big-endian |
| `0x1E` | DateTimeOffset | UTC ticks, 8 bytes, big-endian |
| `0x1F` | DateOnly | Day number, 4 bytes, big-endian |
| `0x20` | TimeOnly | Ticks, 8 bytes, big-endian |
| `0x21` | TimeSpan | Ticks with the sign bit flipped, 8 bytes, big-endian |
| `0x22` | Guid | 16 bytes in the order of its text form |

Integer magnitudes are minimal, with no leading zero byte. A decimal is `0.d1d2…dn × 10^exponent`, with no
leading or trailing zero digit. Floating values have one zero and one NaN, `0x7FF8000000000000`.

### Value format

| Part | Bytes |
| --- | --- |
| Header byte | The width code in its low two bits: `0`, `1`, or `2` for numbers of 1, 2, or 4 bytes; the other bits zero |
| Count | The number of elements, in the width, little-endian |
| Types | One byte per element: its `CompositeElementType` number |
| Ends | Where each element's payload ends, counted from the first payload, in the width, little-endian |
| Payloads | Each element's payload, one after another |

The width is the smallest that holds both the count and the payloads' length.

| Element | Payload |
| --- | --- |
| Null (`0`) | None |
| Bytes (`1`) | The bytes |
| String (`2`) | The UTF-8 bytes |
| Integer (`3`) | Minimal two's complement, little-endian; none for zero |
| Floating (`4`) | IEEE 754, little-endian: 2 bytes for a `Half`, 4 for a `float`, 8 for a `double` |
| Decimal (`5`) | 16 bytes, little-endian: the sign and scale, the high 32 bits of the coefficient, then its low 64 bits |
| Boolean (`6`) | One byte, `0` or `1` |
| DateTime (`7`) | 8 bytes, little-endian: the ticks, with the kind in the top two bits |
| DateTimeOffset (`8`) | The clock ticks, 8 bytes, then the offset in minutes, 2 bytes, little-endian |
| DateOnly (`9`) | The day number, 4 bytes, little-endian |
| TimeOnly (`10`) | The ticks, 8 bytes, little-endian |
| TimeSpan (`11`) | The ticks, 8 bytes, little-endian |
| Guid (`12`) | 16 bytes in the order of `Guid.ToByteArray` |
