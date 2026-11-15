# ZoneTree.Composite

`ZoneTree.Composite` packs several values into one `byte[]`, with no serializer, comparer or hasher to
write or register:

- **`CompositeKey`** encodes a key whose bytes sort as its values do, element by element.
- **`CompositeValue`** encodes a value whose elements are each read directly, without reading the others.

Both produce plain byte arrays. A tree of `byte[]` keys sorts composite keys with its built-in
lexicographic comparer. Each
element records its type, so a key or value is read back without knowing what wrote it.

| | `CompositeKey` | `CompositeValue` |
| --- | --- | --- |
| Use it for | Keys: rows, index entries, key ranges | Values: rows of several fields, index entries that carry fields |
| Order | Bytes sort element by element; elements can be descending | None |
| Reading one element | Walks the elements before it | Direct, from the header |
| Strings and bytes | Zero bytes escaped, so they sort | Stored as they are |
| Round trip | One encoding per value: `1.0m` and `1.00m` are one key | Exact: a decimal keeps its scale, a `DateTime` its kind |
| Joining two | Keys concatenate into one key | Elements are copied with `CompositeValueBuilder.AddElements` |

## Composite keys

```csharp
using ZoneTree.Composite;

var key = CompositeKey.Create("by-customer", "bob", 42L);

var (index, customer, id) = CompositeKey.Decode<string, string, long>(key);
var orderId = CompositeKey.Get<long>(key, 2);
```

### Rows and indexes in one tree

A leading element can name what a key holds. One tree then keeps rows and the entries of several
indexes, each in a key range of its own, and an index entry ends with the elements of its row's key:

```csharp
using ZoneTree;
using ZoneTree.Comparers;
using ZoneTree.Composite;

using var tree = new ZoneTreeFactory<byte[], byte[]>()
    .SetDataDirectory("data/orders")
    .OpenOrCreate();
using var maintainer = tree.CreateMaintainer();

var placed = new DateTime(2026, 11, 15, 9, 30, 0, DateTimeKind.Utc);
tree.Upsert(CompositeKey.Create("row", 42L), CompositeValue.Create("bob", 19.99m, placed));
tree.Upsert(CompositeKey.Create("by-customer", "bob", 42L), CompositeValue.Create(19.99m));
tree.Upsert(CompositeKey.Create("by-time", CompositeKey.Descending(placed), 42L), CompositeValue.Create(19.99m));

// Every order of bob, in order of id.
var (from, to) = CompositeKey.Range(CompositeKey.Create("by-customer", "bob"));
var order = new BytesLexicographicComparerAscending();
using var iterator = tree.CreateIterator();
iterator.Seek(from);
while (iterator.Next() && order.Compare(iterator.CurrentKey, to) <= 0)
{
    var id = CompositeKey.Get<long>(iterator.CurrentKey, 2);
    var total = CompositeValue.Get<decimal>(iterator.CurrentValue, 0);
    Console.WriteLine($"{id}: {total}");
}
```

The application writes an index entry along with its row, and a transaction keeps them together. An
empty byte array is a deletion, so an index entry needs a value of at least one byte. A
`CompositeValue` is never empty, and can carry the fields a query reads.

### Ranges of keys

Two functions bound a scan, and both return inclusive `From` and `To` bounds:

| Function | Holds | Use it for |
| --- | --- | --- |
| `Range(prefix)` | The keys under whole elements: the prefix itself, and every key that goes on from it with more elements | Lookups by leading elements: every order of the customer `"bob"` |
| `BeginsWith(prefix, text)` | The keys whose next element, after the prefix, is a string that begins with the text | Search as the user types: every name that begins with `"bo"` |

```csharp
var bob = CompositeKey.Range(CompositeKey.Create("by-name", "bob"));
// holds ("by-name", "bob") and ("by-name", "bob", 42), not ("by-name", "bobby")

var bo = CompositeKey.BeginsWith(CompositeKey.Create("by-name"), "bo");
// holds ("by-name", "bo"), ("by-name", "bob", 42) and ("by-name", "bobby"), not ("by-name", "bp")
```

`Range` matches whole elements, so a lookup for the customer `"bob"` never returns the orders of
`"bobby"`. `BeginsWith` matches text by Unicode code point, as strings sort. Its other overload takes bytes,
for hash or path prefixes, and both take `CompositeKeyOrder.Descending` as a third argument for a descending
element.

### Descending elements

`CompositeKey.Descending(value)` makes one element sort in reverse, so a newest-first index is
`("by-time", Descending(placed), id)`. Other elements keep their own direction, and the element reads back
as its own type: `Get<DateTime>`, not `Get<Descending<DateTime>>`.

A reverse iterator, or a tree created with `BytesLexicographicComparerDescending`, reverses whole keys
instead.

### Key layouts

A key layout fixes one key space for good: the constant elements its keys start with, then the type and
the order of each position. Keys made through a layout always agree on both, so the same value never
becomes two keys and a layout's ranges hold exactly its keys:

```csharp
static readonly CompositeKeyLayout<string, long> ByCustomer = new(CompositeKey.Create("by-customer"));
static readonly CompositeKeyLayout<DateTime, long> ByTime = new(CompositeKey.Create("by-time"), CompositeKeyOrder.Descending);

tree.Upsert(ByTime.Create(placed, orderId), entry);  // compiles only with a DateTime and a long
var (when, id) = ByTime.Decode(key);                   // checks the prefix, the orders and the element count

var newestFirst = ByTime.Range();                      // every key of the layout
var bobs = ByCustomer.Range("bob");                    // the keys that start with these elements
bool isOrder = ByCustomer.Matches(key);                // whether a key starts with the layout's prefix
```

A layout has a typed form for one to eight positions, and an unsupported type fails when the layout is
made, not when it is first used. Making a key through a layout costs what `Create` costs: the prefix is
encoded once, when the layout is made. A layout's `Range` matches whole elements, as `CompositeKey.Range`
does. `Prefix` and `Order(position)` give what the layout fixed, so its first position, when it is a string,
takes `BeginsWith` as well:

```csharp
var names = CompositeKey.BeginsWith(ByName.Prefix, "bo", ByName.Order(0));
```

### Joining and slicing

A key is its elements one after another, with no header. Two keys joined end to end are the key of their
elements together, and `ElementBytes` returns an element, or a run of elements, as a slice of the key that
is itself a key:

```csharp
var row = CompositeKey.Create("row", 42L);
byte[] entry = [.. CompositeKey.Create("by-customer", "bob"), .. CompositeKey.ElementBytes(row, 1)];

// From the entry back to its row.
byte[] rowKey = [.. CompositeKey.Create("row"), .. CompositeKey.ElementBytes(entry, 2, 1)];
```

### Keys of any length

`Create` and `Decode` take up to eight elements. `CompositeKeyBuilder` builds keys of any length, and keeps
its buffer for the next key after `Clear`:

```csharp
var builder = new CompositeKeyBuilder().Add("by-tags");
foreach (string tag in tags)
    builder.Add(tag);
var key = builder.Add(orderId).ToArray();
```

`Get<T>(key, index)` and `CompositeKeyReader` read keys of any length.

### Inspecting a key

| Member | Gives |
| --- | --- |
| `ElementCount(key)` | The number of elements |
| `ElementType(key, index)`, `IsDescending(key, index)` | An element's type and direction |
| `ElementBytes(key, index)`, `ElementBytes(key, start, count)` | The encoded bytes of an element, or a run of elements |
| `Offsets(key, offsets)` | Where every element starts, in one pass, for direct access afterwards |
| `CompositeKeyReader` | Every element in turn, without allocating |
| `IsValid(key)` | Whether bytes are a well-formed composite key |

A key has no header, so reading element `i` walks the elements before it. For keys of a few elements
that costs little; code that reads many elements of one key uses `Offsets` or the reader.

### How keys sort

- Elements compare one at a time, and a key sorts before every key that extends it.
- Elements of different types sort by type, in the order of `CompositeElementType`: null, bytes, string,
  integer, floating, decimal, boolean, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`,
  `TimeSpan`, `Guid`.
- Ascending elements sort before descending ones at the same position.
- Within a type, elements sort by value:
  - Integers compare by value whatever type wrote them: `5`, `5L`, `(byte)5` and `(Int128)5` are the same
    element. An integer holds at most 255 bytes.
  - Floating values compare by value. `0.0` and `-0.0` are one element, and every NaN is one element,
    which sorts after positive infinity.
  - Decimals compare by value, so `1.0m` and `1.00m` are one key, which reads back as `1`.
  - A `DateTime` sorts by its ticks and reads back with an unspecified kind.
  - A `DateTimeOffset` sorts by its UTC instant: the same instant at any offset is one key, which reads
    back with a zero offset.
  - Strings sort by Unicode code point and must be valid UTF-16. Bytes sort lexicographically.
  - Guids sort as their text forms do.

**Each position of a key layout needs one type and one direction.** A value written at the same position
both ascending and descending, or as an integer and as a string, is two different keys, and the two sort
apart. A `CompositeKeyLayout` keeps every key of its key space to one type and one direction at each
position.

## Composite values

```csharp
using ZoneTree.Composite;

var value = CompositeValue.Create("bob", 19.99m, placed);

var (customer, total, when) = CompositeValue.Decode<string, decimal, DateTime>(value);
var totalOnly = CompositeValue.Get<decimal>(value, 1);  // reads the header, then element 1
```

- **Direct access.** The header gives each element's type and where it ends, so `Get`, `ElementType` and
  `ElementPayload` read one element without touching the others.
- **Payloads as they are.** `ElementPayload(value, index)` returns an element's payload as a slice of the
  value: a string's UTF-8 bytes, or the bytes of a bytes element. Binary payloads are stored without
  escaping, so they add no size.
- **Exact round trips.** A decimal keeps its scale, a `DateTime` its kind, and a `DateTimeOffset` its
  offset. A `float` stays 4 bytes and a `Half` 2, and `-0.0` stays `-0.0`.
- **Minimal integers.** An integer takes its minimal two's complement width: `0` takes no bytes and `127`
  takes one.
- **Growing values.** `Decode` reads the leading elements and leaves the rest unread, so a value grows by
  elements added at its end. `ElementCount` tells a reader whether a later element is there.
- **Never empty.** A value of no elements is two bytes, never the empty array a tree takes for a
  deletion.

`CompositeValueBuilder` builds values of any length. Its `AddElements` copies elements of another value as
they are, without decoding them, which is how a projection keeps some fields of a row:

```csharp
var row = CompositeValue.Create("bob", 19.99m, placed);
var summary = new CompositeValueBuilder()
    .AddElements(row, start: 0, count: 2)  // the customer and the total
    .Add(itemCount)
    .ToArray();
```

### Changing one element

`With` replaces one element of a value, and `Append` adds one at its end. Both copy every other element
as it is, without decoding it, into one new array, so a partial update costs a copy of the bytes, not a
decode and an encode of every field:

```csharp
var cheaper = CompositeValue.With(row, index: 1, 15.99m);  // the same customer and time
var tagged = CompositeValue.Append(row, "gift");           // a field a newer row has
```

`CompositeValueReader` walks the elements of a value, with each element's `ElementType` and `Payload`, and
`IsValid(value)` tells whether bytes are a well-formed composite value.

## Element types

| .NET type | Element type | Notes |
| --- | --- | --- |
| `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `nint`, `nuint`, `Int128`, `UInt128`, `BigInteger`, `char`, enums | Integer | Compared and read by value. An enum is its underlying value. |
| `Half`, `float`, `double` | Floating | |
| `decimal` | Decimal | |
| `bool` | Boolean | |
| `DateTime` | DateTime | |
| `DateTimeOffset` | DateTimeOffset | |
| `DateOnly` | DateOnly | |
| `TimeOnly` | TimeOnly | |
| `TimeSpan` | TimeSpan | |
| `Guid` | Guid | |
| `string` | String | Must be valid UTF-16. |
| `byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>` | Bytes | |
| A null string or byte array, or a nullable value type without a value | Null | Every value type above can be nullable, enums included. |
| `Descending<T>` of any type above | The type's own | Keys only. |

An element's type is the type argument of `Create`, `Add` or `Get`, so an element passed as `object` is
not supported. `Int128` and `UInt128` need .NET 7 or later.

## Reading rules

An element reads as a type that holds its value:

- An integer reads as any integer type that holds it, and as an enum whose underlying type holds it, named
  or not.
- A floating value reads as any floating type that holds it exactly.
- A null element reads as null for strings, byte arrays and nullable value types.
- Every other element reads only as its own type.

| Exception | When |
| --- | --- |
| `InvalidCastException` | The element has another type |
| `OverflowException` | The type cannot hold the element's value exactly |
| `ArgumentOutOfRangeException` | No element at the index, or, in a key, an integer wider than 255 bytes |
| `ArgumentException` | The bytes are malformed, the key or value has fewer elements than `Decode` reads, or a string is not valid UTF-16 or UTF-8 |
| `NotSupportedException` | The type is not supported, or a value is given a descending element |

The members of `CompositeKey`, `CompositeValue` and key layouts are safe for concurrent use. A reader is a stack-only
struct, local to the method that creates it, and a builder is not safe for concurrent use. `Create` measures its
elements first and allocates exactly one array of the right length.

## Formats

The formats are fixed, so any language or tool can read and write them.

### Key format

A key is its elements one after another. Each element is a tag byte and a payload. A descending element
is its ascending bytes complemented, tag included, so descending tags run from `0x80` to `0xFE`. No element
starts with `0x00` or `0xFF`, which is why the byte `0xFF` after a prefix bounds every key that extends it.

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
| `0x18`, `0x19`, `0x1A` | Negative decimal, zero, positive decimal | Nonzero: the exponent plus `0x80`, then the significant digits as ASCII `'0'`–`'9'`, then `0x00`, all complemented for a negative decimal |
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
| Header byte | The width code in its low two bits: `0`, `1` or `2` for numbers of 1, 2 or 4 bytes; the other bits zero |
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
