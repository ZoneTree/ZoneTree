using System.Globalization;
using System.Numerics;
using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Composite;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>Composite values: several values in one byte-array value, each read from its position.</summary>
public sealed class CompositeValueTests
{
  enum Status { Draft = -1, Open = 0, Closed = 7 }

  enum Wide : ulong { Low = 1, High = ulong.MaxValue }

  [Test]
  public void TheFormatIsAHeaderThenThePayloads()
  {
    // Width 1, two elements: an integer and a string, ending at 1 and 2, then their payloads.
    Assert.That(CompositeValue.Create(1, "a"), Is.EqualTo(new byte[] { 0, 2, 3, 2, 1, 2, 1, (byte)'a' }));
    // A value of no elements is two bytes, never the empty array a tree takes for a deletion.
    Assert.That(new CompositeValueBuilder().ToArray(), Is.EqualTo(new byte[] { 0, 0 }));
    Assert.That(CompositeValue.ElementCount([0, 0]), Is.Zero);
    Assert.That(CompositeValue.IsValid([0, 0]), Is.True);
  }

  [Test]
  public void EveryElementReadsBackExactly()
  {
    var utc = new DateTime(2026, 11, 15, 10, 30, 0, DateTimeKind.Utc).AddTicks(7);
    var local = new DateTime(2026, 11, 15, 10, 30, 0, DateTimeKind.Local);
    var instant = new DateTimeOffset(2026, 11, 15, 12, 30, 0, TimeSpan.FromMinutes(-150));
    var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    var value = CompositeValue.Create(1.50m, utc, local, instant, -0.0, 1.5f, (Half)2.5, guid);
    var (number, readUtc, readLocal, readInstant, negativeZero, single, half, readGuid) =
        CompositeValue.Decode<decimal, DateTime, DateTime, DateTimeOffset, double, float, Half, Guid>(value);
    Assert.That(number.ToString(CultureInfo.InvariantCulture), Is.EqualTo("1.50"));
    Assert.That((readUtc.Ticks, readUtc.Kind), Is.EqualTo((utc.Ticks, DateTimeKind.Utc)));
    Assert.That((readLocal.Ticks, readLocal.Kind), Is.EqualTo((local.Ticks, DateTimeKind.Local)));
    Assert.That((readInstant.Ticks, readInstant.Offset), Is.EqualTo((instant.Ticks, instant.Offset)));
    Assert.That(BitConverter.DoubleToInt64Bits(negativeZero), Is.EqualTo(BitConverter.DoubleToInt64Bits(-0.0)));
    Assert.That(single, Is.EqualTo(1.5f));
    Assert.That(half, Is.EqualTo((Half)2.5));
    Assert.That(readGuid, Is.EqualTo(guid));
    // Each floating value keeps its own size.
    Assert.That(CompositeValue.ElementPayload(value, 4).Length, Is.EqualTo(8));
    Assert.That(CompositeValue.ElementPayload(value, 5).Length, Is.EqualTo(4));
    Assert.That(CompositeValue.ElementPayload(value, 6).Length, Is.EqualTo(2));

    byte[] bytes = [0, 0, 1, 0, 255, 0];
    value = CompositeValue.Create("zone\0tree", bytes, new Memory<byte>(bytes), new ReadOnlyMemory<byte>(bytes),
        (string)null, (byte[])null, (int?)null, (Guid?)guid);
    Assert.That(CompositeValue.Get<string>(value, 0), Is.EqualTo("zone\0tree"));
    Assert.That(CompositeValue.Get<byte[]>(value, 1), Is.EqualTo(bytes));
    Assert.That(CompositeValue.Get<Memory<byte>>(value, 2).ToArray(), Is.EqualTo(bytes));
    Assert.That(CompositeValue.Get<ReadOnlyMemory<byte>>(value, 3).ToArray(), Is.EqualTo(bytes));
    Assert.That(CompositeValue.Get<string>(value, 4), Is.Null);
    Assert.That(CompositeValue.Get<byte[]>(value, 5), Is.Null);
    Assert.That(CompositeValue.Get<long?>(value, 6), Is.Null);
    Assert.That(CompositeValue.Get<Guid?>(value, 7), Is.EqualTo(guid));
    // Strings and bytes are stored as they are, with nothing escaped.
    Assert.That(CompositeValue.ElementPayload(value, 0).ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes("zone\0tree")));
    Assert.That(CompositeValue.ElementPayload(value, 1).ToArray(), Is.EqualTo(bytes));

    value = CompositeValue.Create(true, false, new DateOnly(2026, 11, 15), new TimeOnly(23, 59, 59, 999), TimeSpan.FromTicks(-5),
        Status.Draft, (Status?)Status.Closed, Wide.High);
    Assert.That(CompositeValue.Decode<bool, bool, DateOnly, TimeOnly, TimeSpan, Status, Status?, Wide>(value),
        Is.EqualTo((true, false, new DateOnly(2026, 11, 15), new TimeOnly(23, 59, 59, 999), TimeSpan.FromTicks(-5),
            Status.Draft, (Status?)Status.Closed, Wide.High)));
  }

  [Test]
  public void IntegersTakeTheirMinimalWidthAndReadAsAnyTypeThatHoldsThem()
  {
    (object Number, int Width)[] widths =
    [
      (0, 0), (1, 1), (-1, 1), (127, 1), (-128, 1), (128, 2), (-129, 2), (long.MaxValue, 8), (long.MinValue, 8),
      ((ulong)long.MaxValue + 1, 9), (ulong.MaxValue, 9), (Int128.MaxValue, 16), (Int128.MinValue, 16),
      (UInt128.MaxValue, 17), (BigInteger.Pow(2, 300), 38)
    ];
    foreach (var (number, width) in widths)
    {
      var value = number switch
      {
        int int32 => CompositeValue.Create(int32),
        long int64 => CompositeValue.Create(int64),
        ulong uint64 => CompositeValue.Create(uint64),
        Int128 int128 => CompositeValue.Create(int128),
        UInt128 uint128 => CompositeValue.Create(uint128),
        _ => CompositeValue.Create((BigInteger)number)
      };
      Assert.That(CompositeValue.ElementPayload(value, 0).Length, Is.EqualTo(width), $"{number}");
      Assert.That(CompositeValue.Get<BigInteger>(value, 0), Is.EqualTo(number switch
      {
        int int32 => (BigInteger)int32,
        long int64 => int64,
        ulong uint64 => uint64,
        Int128 int128 => int128,
        UInt128 uint128 => uint128,
        _ => (BigInteger)number
      }));
    }

    // Every integer type writes the same bytes for the same value.
    Assert.That(CompositeValue.Create((byte)5), Is.EqualTo(CompositeValue.Create(5L)));
    Assert.That(CompositeValue.Create((Int128)(-300)), Is.EqualTo(CompositeValue.Create(-300)));
    Assert.That(CompositeValue.Create(new BigInteger(-300)), Is.EqualTo(CompositeValue.Create(-300)));
    Assert.That(CompositeValue.Create(Status.Closed), Is.EqualTo(CompositeValue.Create(7)));
    Assert.That(CompositeValue.Create('Ж'), Is.EqualTo(CompositeValue.Create(0x416)));
    Assert.That(CompositeValue.Create((nint)(-7)), Is.EqualTo(CompositeValue.Create(-7)));

    Assert.That(CompositeValue.Get<byte>(CompositeValue.Create(200L), 0), Is.EqualTo(200));
    Assert.That(CompositeValue.Get<ulong>(CompositeValue.Create(ulong.MaxValue), 0), Is.EqualTo(ulong.MaxValue));
    Assert.That(CompositeValue.Get<UInt128>(CompositeValue.Create(UInt128.MaxValue), 0), Is.EqualTo(UInt128.MaxValue));
    Assert.That(CompositeValue.Get<UInt128>(CompositeValue.Create(ulong.MaxValue), 0), Is.EqualTo((UInt128)ulong.MaxValue));
    Assert.That(CompositeValue.Get<Int128>(CompositeValue.Create(Int128.MinValue), 0), Is.EqualTo(Int128.MinValue));
    Assert.That(CompositeValue.Get<Int128>(CompositeValue.Create(-5), 0), Is.EqualTo((Int128)(-5)));
    Assert.That(CompositeValue.Get<char>(CompositeValue.Create(0x416), 0), Is.EqualTo('Ж'));
    Assert.That(CompositeValue.Get<nuint>(CompositeValue.Create(7), 0), Is.EqualTo((nuint)7));
    Assert.That(CompositeValue.Get<Status>(CompositeValue.Create(42), 0), Is.EqualTo((Status)42));
    Assert.Throws<OverflowException>(() => CompositeValue.Get<byte>(CompositeValue.Create(300), 0));
    Assert.Throws<OverflowException>(() => CompositeValue.Get<uint>(CompositeValue.Create(-1), 0));
    Assert.Throws<OverflowException>(() => CompositeValue.Get<long>(CompositeValue.Create(ulong.MaxValue), 0));
    Assert.Throws<OverflowException>(() => CompositeValue.Get<ulong>(CompositeValue.Create(BigInteger.Pow(2, 64)), 0));
    Assert.Throws<OverflowException>(() => CompositeValue.Get<Int128>(CompositeValue.Create(UInt128.MaxValue), 0));
    Assert.Throws<OverflowException>(() => CompositeValue.Get<UInt128>(CompositeValue.Create(-1), 0));
    Assert.Throws<OverflowException>(() => CompositeValue.Get<UInt128>(CompositeValue.Create(BigInteger.Pow(2, 128)), 0));
    Assert.Throws<OverflowException>(() => CompositeValue.Get<Status>(CompositeValue.Create(long.MaxValue), 0));
  }

  [Test]
  public void FloatingValuesReadAsAnyTypeThatHoldsThemExactly()
  {
    Assert.That(CompositeValue.Get<double>(CompositeValue.Create(1.5f), 0), Is.EqualTo(1.5));
    Assert.That(CompositeValue.Get<double>(CompositeValue.Create((Half)1.5), 0), Is.EqualTo(1.5));
    Assert.That(CompositeValue.Get<float>(CompositeValue.Create(0.25), 0), Is.EqualTo(0.25f));
    Assert.That(CompositeValue.Get<Half>(CompositeValue.Create(0.25f), 0), Is.EqualTo((Half)0.25));
    Assert.That(float.IsNaN(CompositeValue.Get<float>(CompositeValue.Create(double.NaN), 0)), Is.True);
    Assert.Throws<OverflowException>(() => CompositeValue.Get<float>(CompositeValue.Create(0.1), 0));
    Assert.Throws<OverflowException>(() => CompositeValue.Get<Half>(CompositeValue.Create(0.1f), 0));
    Assert.Throws<InvalidCastException>(() => CompositeValue.Get<double>(CompositeValue.Create(1), 0));
    Assert.Throws<InvalidCastException>(() => CompositeValue.Get<decimal>(CompositeValue.Create(1.5), 0));
  }

  [Test]
  public void TheHeaderReachesEveryElement()
  {
    var value = CompositeValue.Create("first", 2L, (string)null, new byte[] { 4 }, 5.5, true, Guid.Empty, "last");
    CompositeElementType[] types =
    [
      CompositeElementType.String, CompositeElementType.Integer, CompositeElementType.Null, CompositeElementType.Bytes,
      CompositeElementType.Floating, CompositeElementType.Boolean, CompositeElementType.Guid, CompositeElementType.String
    ];
    Assert.That(CompositeValue.ElementCount(value), Is.EqualTo(types.Length));
    for (var i = 0; i < types.Length; ++i)
      Assert.That(CompositeValue.ElementType(value, i), Is.EqualTo(types[i]));
    Assert.That(CompositeValue.Get<string>(value, 7), Is.EqualTo("last"));
    Assert.That(CompositeValue.Get<long>(value, 1), Is.EqualTo(2));

    var reader = new CompositeValueReader(value);
    Assert.That(reader.Count, Is.EqualTo(types.Length));
    for (var i = 0; i < types.Length; ++i)
    {
      Assert.That(reader.Next(), Is.True);
      Assert.That(reader.Index, Is.EqualTo(i));
      Assert.That(reader.ElementType, Is.EqualTo(types[i]));
      Assert.That(reader.Payload.ToArray(), Is.EqualTo(CompositeValue.ElementPayload(value, i).ToArray()));
    }
    Assert.That(reader.Read<string>(), Is.EqualTo("last"));
    Assert.That(reader.Next(), Is.False);
    Assert.That(reader.Next(), Is.False);
    Assert.Throws<InvalidOperationException>(() => new CompositeValueReader(value).Read<string>());

    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeValue.Get<string>(value, 8));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeValue.Get<string>(value, -1));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeValue.ElementType(value, 8));
  }

  [Test]
  public void TheHeaderWidensWithTheValue()
  {
    var small = CompositeValue.Create(new string('x', 200));
    var medium = CompositeValue.Create(new string('x', 300), 1);
    var large = CompositeValue.Create(new byte[70_000], "end");
    Assert.That(small[0], Is.Zero);
    Assert.That(medium[0], Is.EqualTo(1));
    Assert.That(large[0], Is.EqualTo(2));
    Assert.That(CompositeValue.Get<string>(medium, 0), Is.EqualTo(new string('x', 300)));
    Assert.That(CompositeValue.Get<int>(medium, 1), Is.EqualTo(1));
    Assert.That(CompositeValue.Get<byte[]>(large, 0), Has.Length.EqualTo(70_000));
    Assert.That(CompositeValue.Get<string>(large, 1), Is.EqualTo("end"));

    // Many elements widen it too, even with no payloads.
    var builder = new CompositeValueBuilder();
    for (var i = 0; i < 300; ++i)
      builder.Add((string)null);
    var nulls = builder.ToArray();
    Assert.That(nulls[0], Is.EqualTo(1));
    Assert.That(CompositeValue.ElementCount(nulls), Is.EqualTo(300));
    Assert.That(CompositeValue.Get<string>(nulls, 299), Is.Null);
    Assert.That(CompositeValue.IsValid(nulls), Is.True);
  }

  [Test]
  public void DecodeReadsTheLeadingElements()
  {
    var value = CompositeValue.Create("acme", 42L, 19.99m);
    Assert.That(CompositeValue.Decode<string>(value), Is.EqualTo("acme"));
    Assert.That(CompositeValue.Decode<string, long>(value), Is.EqualTo(("acme", 42L)));
    Assert.That(CompositeValue.Decode<string, int, decimal>(value), Is.EqualTo(("acme", 42, 19.99m)));
    var tooFew = Assert.Throws<ArgumentException>(() => CompositeValue.Decode<string, long, decimal, int>(value));
    Assert.That(tooFew.Message, Does.Contain("3 elements, fewer than 4"));
    Assert.Throws<InvalidCastException>(() => CompositeValue.Decode<long, long>(value));
    Assert.Throws<InvalidCastException>(() => CompositeValue.Get<long>(CompositeValue.Create((string)null), 0));
    Assert.Throws<NotSupportedException>(() => CompositeValue.Get<object>(value, 0));
    Assert.Throws<NotSupportedException>(() => CompositeValue.Get<Descending<string>>(value, 0));
    Assert.Throws<NotSupportedException>(() => CompositeValue.Create(new Version(1, 2)));
    Assert.Throws<NotSupportedException>(() => CompositeValue.Create(CompositeKey.Descending(1)));
    Assert.Throws<ArgumentException>(() => CompositeValue.Create("\uD800"));
  }

  [Test]
  public void TryReadsReturnFalseWhereReadsThrow()
  {
    var value = CompositeValue.Create("bob", 300L, 19.99m, 0.1f);
    Assert.That(CompositeValue.TryDecode(value, out string name), Is.True);
    Assert.That(name, Is.EqualTo("bob"));
    Assert.That(CompositeValue.TryDecode(value, out (string, int, decimal) three), Is.True);
    Assert.That(three, Is.EqualTo(("bob", 300, 19.99m)));
    Assert.That(CompositeValue.TryGet(value, 3, out float single), Is.True);
    Assert.That(single, Is.EqualTo(0.1f));

    // Each failure leaves the default behind.
    Assert.That(CompositeValue.TryDecode(value, out (string, long, decimal, float, int) tooFew), Is.False);
    Assert.That(tooFew, Is.EqualTo(default((string, long, decimal, float, int))));
    Assert.That(CompositeValue.TryDecode(value, out (string, string) mismatch), Is.False);
    Assert.That(mismatch, Is.EqualTo(default((string, string))));
    Assert.That(CompositeValue.TryGet(value, 1, out byte overflow), Is.False);
    Assert.That(overflow, Is.Zero);
    Assert.That(CompositeValue.TryGet(value, 3, out Half _), Is.False);
    Assert.That(CompositeValue.TryGet(value, 4, out string _), Is.False);
    Assert.That(CompositeValue.TryGet(value, -1, out string _), Is.False);
    Assert.That(CompositeValue.TryDecode<string>(new CompositeValueBuilder().ToArray(), out _), Is.False);

    // A malformed header fails every read, a malformed element the reads of it, and a string of invalid UTF-8 as it
    // is read.
    Assert.That(CompositeValue.TryGet([3, 0], 0, out int _), Is.False);
    Assert.That(CompositeValue.TryDecode([3, 0], out (int, int) _), Is.False);
    byte[] malformedGuid = [0, 2, 3, 12, 1, 4, 7, 1, 2, 3];
    Assert.That(CompositeValue.TryGet(malformedGuid, 0, out int integer), Is.True);
    Assert.That(integer, Is.EqualTo(7));
    Assert.That(CompositeValue.TryGet(malformedGuid, 1, out Guid _), Is.False);
    Assert.That(CompositeValue.TryDecode(malformedGuid, out (int, Guid) _), Is.False);
    byte[] invalidUtf8 = [0, 1, 2, 2, 0xC3, 0x28];
    Assert.That(CompositeValue.TryGet(invalidUtf8, 0, out string _), Is.False);

    // A type no element is, is a mistake in the code, so it still throws.
    Assert.Throws<NotSupportedException>(() => CompositeValue.TryGet<object>([3, 0], 0, out _));
    Assert.Throws<NotSupportedException>(() => CompositeValue.TryDecode<string, Descending<int>>(value, out _));

    var reader = new CompositeValueReader(malformedGuid);
    Assert.That(reader.Next(), Is.True);
    Assert.That(reader.TryRead(out string wrong), Is.False);
    Assert.That(wrong, Is.Null);
    Assert.That(reader.TryRead(out long number), Is.True);
    Assert.That(number, Is.EqualTo(7));
    Assert.That(reader.Next(), Is.True);
    Assert.That(reader.TryRead(out Guid _), Is.False);

    var eight = CompositeValue.Create(1, 2, 3, 4, 5, 6, 7, 8);
    Assert.That(CompositeValue.TryDecode(eight, out (int, int) two), Is.True);
    Assert.That(two, Is.EqualTo((1, 2)));
    Assert.That(CompositeValue.TryDecode(eight, out (int, int, int, int) four), Is.True);
    Assert.That(four, Is.EqualTo((1, 2, 3, 4)));
    Assert.That(CompositeValue.TryDecode(eight, out (int, int, int, int, int) five), Is.True);
    Assert.That(five, Is.EqualTo((1, 2, 3, 4, 5)));
    Assert.That(CompositeValue.TryDecode(eight, out (int, int, int, int, int, int) six), Is.True);
    Assert.That(six, Is.EqualTo((1, 2, 3, 4, 5, 6)));
    Assert.That(CompositeValue.TryDecode(eight, out (int, int, int, int, int, int, int) seven), Is.True);
    Assert.That(seven, Is.EqualTo((1, 2, 3, 4, 5, 6, 7)));
    Assert.That(CompositeValue.TryDecode(eight, out (int, int, int, int, int, int, int, int) all), Is.True);
    Assert.That(all, Is.EqualTo((1, 2, 3, 4, 5, 6, 7, 8)));
  }

  [Test]
  public void FormatShowsEveryElementAsWritten()
  {
    var value = new CompositeValueBuilder()
        .Add("bob")
        .Add(19.90m)
        .Add(new DateTime(2026, 11, 15, 9, 30, 0, DateTimeKind.Utc))
        .Add(new DateTime(2026, 11, 15, 9, 30, 0, DateTimeKind.Local))
        .Add(new DateTime(2026, 11, 15, 9, 30, 0))
        .Add(new DateTimeOffset(2026, 11, 15, 9, 30, 0, TimeSpan.FromHours(3)))
        .Add(0.1f)
        .Add((Half)0.5)
        .Add(2.0)
        .Add(-0.0)
        .Add((string)null)
        .Add(new byte[] { 1, 2 })
        .Add(ulong.MaxValue)
        .ToArray();
    Assert.That(CompositeValue.Format(value), Is.EqualTo(
        """("bob", 19.90m, 2026-11-15T09:30:00.0000000Z, 2026-11-15T09:30:00.0000000 local, 2026-11-15T09:30:00.0000000, 2026-11-15T09:30:00.0000000+03:00, 0.1, 0.5, 2.0, -0.0, null, 0x0102, 18446744073709551615)"""));

    // Any bytes have a text: a malformed element shows its payload, and a malformed header the whole bytes.
    Assert.That(CompositeValue.Format(new CompositeValueBuilder().ToArray()), Is.EqualTo("()"));
    Assert.That(CompositeValue.Format([0, 2, 3, 12, 1, 4, 7, 1, 2, 3]), Is.EqualTo("(7, malformed(0x010203))"));
    Assert.That(CompositeValue.Format([0, 1, 2, 2, 0xC3, 0x28]), Is.EqualTo("(malformed(0xC328))"));
    Assert.That(CompositeValue.Format([3, 0]), Is.EqualTo("(malformed(0x0300))"));
    Assert.That(CompositeValue.Format([0, 3, 3, 3, 3, 2, 1, 3, 7, 8, 9]), Is.EqualTo("(malformed(0x0003030303020103070809))"));
  }

  [Test]
  public void TheBuilderBuildsWhatCreateBuildsAndMore()
  {
    var placed = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(3));
    var builder = new CompositeValueBuilder();
    Assert.That(builder.Count, Is.Zero);
    var value = builder.Add("bob").Add(42L).Add((string)null).Add(placed).Add(new byte[] { 0, 1 }).ToArray();
    Assert.That(value, Is.EqualTo(CompositeValue.Create("bob", 42L, (string)null, placed, new byte[] { 0, 1 })));
    Assert.That(builder.Count, Is.EqualTo(5));

    // Any number of elements, read by index.
    builder.Clear();
    for (var i = 0; i < 40; ++i)
      builder.Add(i % 2 == 0 ? i.ToString(CultureInfo.InvariantCulture) : null);
    var many = builder.ToArray();
    Assert.That(CompositeValue.ElementCount(many), Is.EqualTo(40));
    for (var i = 0; i < 40; ++i)
      Assert.That(CompositeValue.Get<string>(many, i), Is.EqualTo(i % 2 == 0 ? i.ToString(CultureInfo.InvariantCulture) : null));

    // Elements of another value are copied as they are.
    var projected = new CompositeValueBuilder().Add("total").AddElements(value, 1, 3).ToArray();
    Assert.That(projected, Is.EqualTo(CompositeValue.Create("total", 42L, (string)null, placed)));
    Assert.That(new CompositeValueBuilder().AddElements(value, 5, 0).ToArray(), Is.EqualTo(new byte[] { 0, 0 }));
    Assert.Throws<ArgumentOutOfRangeException>(() => new CompositeValueBuilder().AddElements(value, 4, 2));
    Assert.Throws<ArgumentOutOfRangeException>(() => new CompositeValueBuilder().AddElements(value, 6, 0));
    Assert.Throws<ArgumentException>(() => new CompositeValueBuilder().AddElements([9, 9], 0, 0));

    // A failed append leaves the value as it was.
    builder.Clear().Add("kept");
    var before = builder.ToArray();
    // An integer, then a Guid of 3 bytes: neither is copied.
    byte[] malformedSource = [0, 2, 3, 12, 1, 4, 7, 1, 2, 3];
    Assert.Throws<ArgumentException>(() => builder.AddElements(malformedSource, 0, 2));
    Assert.That(builder.ToArray(), Is.EqualTo(before));
    Assert.That(CompositeValue.Get<int>(new CompositeValueBuilder().AddElements(malformedSource, 0, 1).ToArray(), 0), Is.EqualTo(7));
    Assert.Throws<NotSupportedException>(() => builder.Add(new Version(1, 2)));
    Assert.Throws<NotSupportedException>(() => builder.Add(CompositeKey.Descending(1)));
    Assert.Throws<ArgumentException>(() => builder.Add("\uD800"));
    Assert.That(builder.ToArray(), Is.EqualTo(before));
    Assert.Throws<ArgumentOutOfRangeException>(() => new CompositeValueBuilder(-1));
    Assert.That(new CompositeValueBuilder(4).Add(new string('y', 100)).ToArray(), Is.EqualTo(CompositeValue.Create(new string('y', 100))));
  }

  [Test]
  public void WithReplacesOneElementAndCopiesTheOthers()
  {
    var placed = new DateTime(2026, 11, 15, 9, 30, 0, DateTimeKind.Utc);
    byte[] bytes = [0, 1, 2];
    var row = CompositeValue.Create("bob", 19.99m, placed, bytes);
    Assert.That(CompositeValue.With(row, 1, 5m), Is.EqualTo(CompositeValue.Create("bob", 5m, placed, bytes)));
    Assert.That(CompositeValue.With(row, 0, "alexandra"), Is.EqualTo(CompositeValue.Create("alexandra", 19.99m, placed, bytes)));
    Assert.That(CompositeValue.With(row, 3, 42L), Is.EqualTo(CompositeValue.Create("bob", 19.99m, placed, 42L)));
    Assert.That(CompositeValue.With(row, 2, (string)null), Is.EqualTo(CompositeValue.Create("bob", 19.99m, (string)null, bytes)));

    // A value that grows past 255 bytes widens its header, and narrows it again as it shrinks.
    var wide = CompositeValue.With(row, 0, new string('x', 300));
    Assert.That(wide[0], Is.EqualTo(1));
    Assert.That(wide, Is.EqualTo(CompositeValue.Create(new string('x', 300), 19.99m, placed, bytes)));
    Assert.That(CompositeValue.With(wide, 0, "bob"), Is.EqualTo(row));

    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeValue.With(row, 4, 1));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeValue.With(row, -1, 1));
    Assert.Throws<NotSupportedException>(() => CompositeValue.With(row, 0, new Version(1, 2)));
    Assert.Throws<NotSupportedException>(() => CompositeValue.With(row, 0, CompositeKey.Descending(1)));

    // An integer, then a Guid of 3 bytes: the malformed Guid fails a change of another element, and its
    // replacement makes the value whole.
    byte[] malformed = [0, 2, 3, 12, 1, 4, 7, 1, 2, 3];
    Assert.Throws<ArgumentException>(() => CompositeValue.With(malformed, 0, 8));
    var repaired = CompositeValue.With(malformed, 1, Guid.Empty);
    Assert.That(CompositeValue.IsValid(repaired), Is.True);
    Assert.That(CompositeValue.Decode<int, Guid>(repaired), Is.EqualTo((7, Guid.Empty)));

    // Three integers whose middle one ends before it starts: the elements around it are whole, and its
    // replacement is refused rather than built over the bytes they hold.
    byte[] backwards = [0, 3, 3, 3, 3, 2, 1, 3, 7, 8, 9];
    Assert.That(CompositeValue.IsValid(backwards), Is.False);
    Assert.Throws<ArgumentException>(() => CompositeValue.With(backwards, 1, 5));
  }

  [Test]
  public void AppendAddsAnElementAtTheEnd()
  {
    var row = CompositeValue.Create("bob", 19.99m);
    var grown = CompositeValue.Append(row, "gift");
    Assert.That(grown, Is.EqualTo(CompositeValue.Create("bob", 19.99m, "gift")));
    Assert.That(CompositeValue.Decode<string, decimal>(grown), Is.EqualTo(("bob", 19.99m)));
    Assert.That(CompositeValue.Append(new CompositeValueBuilder().ToArray(), 7), Is.EqualTo(CompositeValue.Create(7)));
    var wide = CompositeValue.Append(row, new byte[300]);
    Assert.That(wide[0], Is.EqualTo(1));
    Assert.That(wide, Is.EqualTo(CompositeValue.Create("bob", 19.99m, new byte[300])));
    Assert.Throws<NotSupportedException>(() => CompositeValue.Append(row, new Version(1, 2)));
    Assert.Throws<ArgumentException>(() => CompositeValue.Append([0, 2, 3, 12, 1, 4, 7, 1, 2, 3], 8));
  }

  [Test]
  public void IsValidTellsCompositeValuesFromMalformedBytes()
  {
    byte[][] malformed =
    [
      // Headers: too short, an unknown width, other header bits, a count past the bytes, and ends that do not
      // end at the bytes' end.
      [],
      [0],
      [3, 0],
      [4, 0],
      [0, 5, 2],
      [0, 1, 3, 2, 1],
      [0, 1, 3, 0, 1],
      // Ends out of order, though the last one ends where the bytes do.
      [0, 3, 3, 3, 3, 2, 1, 2, 7, 8],
      // Payloads not of their types: an unknown type, a null with bytes, a Guid of 3 bytes, a bool of 2, a floating
      // value of 3 bytes, a decimal of scale 29, a DateTime of the fourth kind, a date past the last day, a
      // time of day past midnight, and points in time offset by short.MinValue minutes and by one minute past 14
      // hours.
      [0, 1, 13, 0],
      [0, 1, 0, 1, 7],
      [0, 1, 12, 3, 1, 2, 3],
      [0, 1, 6, 1, 2],
      [0, 1, 4, 3, 1, 2, 3],
      [0, 1, 5, 16, 0, 0, 29, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
      [0, 1, 7, 8, 0, 0, 0, 0, 0, 0, 0, 0xC0],
      [0, 1, 9, 4, 0xFF, 0xFF, 0xFF, 0x7F],
      [0, 1, 10, 8, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F],
      [0, 1, 8, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x80],
      [0, 1, 8, 10, 0, 0x40, 0x7A, 0x10, 0xF3, 0x5A, 0, 0, 0x49, 0x03],
    ];
    foreach (var bytes in malformed)
      Assert.That(CompositeValue.IsValid(bytes), Is.False, Convert.ToHexString(bytes));
    Assert.Throws<ArgumentException>(() => CompositeValue.ElementCount([3, 0]));
    Assert.Throws<ArgumentException>(() => CompositeValue.Get<Guid>([0, 1, 12, 3, 1, 2, 3], 0));
    Assert.Throws<ArgumentException>(() => CompositeValue.Get<string>([0, 1, 13, 0], 0));
    Assert.Throws<ArgumentException>(() => CompositeValue.ElementType([0, 1, 13, 0], 0));
    // An offset of short.MinValue minutes is malformed wherever the element is checked: as it is read, and as
    // another element of its value changes.
    Assert.Throws<ArgumentException>(() => CompositeValue.Get<DateTimeOffset>([0, 1, 8, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x80], 0));
    Assert.Throws<ArgumentException>(() => CompositeValue.With([0, 2, 3, 8, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x80], 0, 5));

    // A string's UTF-8 is checked when it is read.
    byte[] invalidUtf8 = [0, 1, 2, 2, 0xC3, 0x28];
    Assert.That(CompositeValue.IsValid(invalidUtf8), Is.False);
    Assert.That(CompositeValue.ElementCount(invalidUtf8), Is.EqualTo(1));
    Assert.Throws<ArgumentException>(() => CompositeValue.Get<string>(invalidUtf8, 0));
  }

  [Test]
  public void RandomValuesReadBack()
  {
    var random = new Random(20261115);
    for (var round = 0; round < 300; ++round)
    {
      var elements = Enumerable.Range(0, random.Next(0, 12)).Select(_ => RandomElement(random)).ToArray();
      var builder = new CompositeValueBuilder();
      foreach (var element in elements)
        element.Add(builder);
      var value = builder.ToArray();
      Assert.That(CompositeValue.IsValid(value), Is.True);
      Assert.That(CompositeValue.ElementCount(value), Is.EqualTo(elements.Length));
      for (var i = 0; i < elements.Length; ++i)
      {
        Assert.That(CompositeValue.ElementType(value, i), Is.EqualTo(elements[i].Type));
        elements[i].AssertReadsBack(value, i);
      }
    }
  }

  /// <summary>An element of a random value: how it is added, its type, and how its reading is checked.</summary>
  sealed record Element(CompositeElementType Type, Action<CompositeValueBuilder> Add, Action<byte[], int> AssertReadsBack);

  static Element RandomElement(Random random)
  {
    switch (random.Next(13))
    {
      case 0:
        return new(CompositeElementType.Null, b => b.Add((string)null), (v, i) => Assert.That(CompositeValue.Get<string>(v, i), Is.Null));
      case 1:
        var bytes = Enumerable.Range(0, random.Next(0, 40)).Select(_ => (byte)random.Next(3)).ToArray();
        return new(CompositeElementType.Bytes, b => b.Add(bytes), (v, i) => Assert.That(CompositeValue.Get<byte[]>(v, i), Is.EqualTo(bytes)));
      case 2:
        var text = string.Concat(Enumerable.Range(0, random.Next(0, 20)).Select(_ => "a\0é\U0001F600"[random.Next(4)].ToString()));
        text = text.Replace("\uD83D", "", StringComparison.Ordinal).Replace("\uDE00", "", StringComparison.Ordinal);
        return new(CompositeElementType.String, b => b.Add(text), (v, i) => Assert.That(CompositeValue.Get<string>(v, i), Is.EqualTo(text)));
      case 3:
        var integer = new BigInteger(random.NextInt64(long.MinValue, long.MaxValue)) * random.Next(-3, 4) * BigInteger.Pow(2, random.Next(0, 80));
        return new(CompositeElementType.Integer, b => b.Add(integer), (v, i) => Assert.That(CompositeValue.Get<BigInteger>(v, i), Is.EqualTo(integer)));
      case 4:
        var floating = random.NextDouble() * random.Next(-1000, 1000);
        return new(CompositeElementType.Floating, b => b.Add(floating), (v, i) => Assert.That(CompositeValue.Get<double>(v, i), Is.EqualTo(floating)));
      case 5:
        var number = new decimal(random.Next(), random.Next(), random.Next(), random.Next(2) == 1, (byte)random.Next(29));
        return new(CompositeElementType.Decimal, b => b.Add(number), (v, i) =>
            Assert.That(CompositeValue.Get<decimal>(v, i).ToString(CultureInfo.InvariantCulture), Is.EqualTo(number.ToString(CultureInfo.InvariantCulture))));
      case 6:
        var boolean = random.Next(2) == 1;
        return new(CompositeElementType.Boolean, b => b.Add(boolean), (v, i) => Assert.That(CompositeValue.Get<bool>(v, i), Is.EqualTo(boolean)));
      case 7:
        var time = new DateTime(random.NextInt64(DateTime.MaxValue.Ticks), (DateTimeKind)random.Next(3));
        return new(CompositeElementType.DateTime, b => b.Add(time), (v, i) =>
            Assert.That((CompositeValue.Get<DateTime>(v, i).Ticks, CompositeValue.Get<DateTime>(v, i).Kind), Is.EqualTo((time.Ticks, time.Kind))));
      case 8:
        var instant = new DateTimeOffset(random.NextInt64(TimeSpan.TicksPerDay, DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay),
            TimeSpan.FromMinutes(random.Next(-14 * 60, 14 * 60)));
        return new(CompositeElementType.DateTimeOffset, b => b.Add(instant), (v, i) =>
            Assert.That((CompositeValue.Get<DateTimeOffset>(v, i).Ticks, CompositeValue.Get<DateTimeOffset>(v, i).Offset), Is.EqualTo((instant.Ticks, instant.Offset))));
      case 9:
        var date = DateOnly.FromDayNumber(random.Next(DateOnly.MaxValue.DayNumber));
        return new(CompositeElementType.DateOnly, b => b.Add(date), (v, i) => Assert.That(CompositeValue.Get<DateOnly>(v, i), Is.EqualTo(date)));
      case 10:
        var timeOfDay = new TimeOnly(random.NextInt64(TimeOnly.MaxValue.Ticks));
        return new(CompositeElementType.TimeOnly, b => b.Add(timeOfDay), (v, i) => Assert.That(CompositeValue.Get<TimeOnly>(v, i), Is.EqualTo(timeOfDay)));
      case 11:
        var interval = new TimeSpan(random.NextInt64(long.MinValue, long.MaxValue));
        return new(CompositeElementType.TimeSpan, b => b.Add(interval), (v, i) => Assert.That(CompositeValue.Get<TimeSpan>(v, i), Is.EqualTo(interval)));
      default:
        var guid = Guid.NewGuid();
        return new(CompositeElementType.Guid, b => b.Add(guid), (v, i) => Assert.That(CompositeValue.Get<Guid>(v, i), Is.EqualTo(guid)));
    }
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void RowsKeepTheirCompositeValuesThroughMergesAndReloads(DiskSegmentMode mode)
  {
    var provider = new InMemoryFileStreamProvider();
    (long Id, string Customer, decimal Total, DateTimeOffset Placed)[] orders =
    [
      (1, "bob", 19.99m, new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(3))),
      (2, "alice", 5.50m, new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero)),
      (3, "carol", 120.00m, new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(-5))),
      (4, "bob", 0.01m, new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.FromHours(9))),
    ];
    using (var tree = CreateFactory(provider, mode).Create())
    {
      foreach (var (id, customer, total, placed) in orders)
        tree.Upsert(CompositeKey.Create("row", id), CompositeValue.Create(customer, total, placed));
      AssertRows(tree, orders);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
      AssertRows(tree, orders);
    }
    using (var tree = CreateFactory(provider, mode).Open())
      AssertRows(tree, orders);
  }

  static void AssertRows(IZoneTree<byte[], byte[]> tree, (long Id, string Customer, decimal Total, DateTimeOffset Placed)[] orders)
  {
    foreach (var (id, customer, total, placed) in orders)
    {
      Assert.That(tree.TryGet(CompositeKey.Create("row", id), out var value), Is.True);
      var (readCustomer, readTotal, readPlaced) = CompositeValue.Decode<string, decimal, DateTimeOffset>(value);
      Assert.That(readCustomer, Is.EqualTo(customer));
      Assert.That(readTotal.ToString(CultureInfo.InvariantCulture), Is.EqualTo(total.ToString(CultureInfo.InvariantCulture)));
      Assert.That((readPlaced.Ticks, readPlaced.Offset), Is.EqualTo((placed.Ticks, placed.Offset)));
    }
  }

  static ZoneTreeFactory<byte[], byte[]> CreateFactory(InMemoryFileStreamProvider provider, DiskSegmentMode mode)
  {
    return new ZoneTreeFactory<byte[], byte[]>(provider)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.SyncCompressed)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DiskSegmentMode = mode;
          options.MinimumRecordCount = 2;
          options.MaximumRecordCount = 3;
        });
  }
}
