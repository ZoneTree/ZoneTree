using System.Globalization;
using System.Numerics;
using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Composite;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>Composite keys: several values in one byte-array key that sorts as they do.</summary>
public sealed class CompositeKeyTests
{
  static readonly BytesLexicographicComparerAscending Order = new();

  [Test]
  public void EveryElementTypeReadsBackAsWritten()
  {
    var guid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    var time = new DateTime(2026, 11, 15, 10, 30, 0, DateTimeKind.Utc);
    var key = CompositeKey.Create(true, (sbyte)-5, (byte)200, (short)-30000, (ushort)60000, -2_000_000_000, 4_000_000_000u, long.MinValue);
    Assert.That(CompositeKey.Decode<bool, sbyte, byte, short, ushort, int, uint, long>(key),
        Is.EqualTo((true, (sbyte)-5, (byte)200, (short)-30000, (ushort)60000, -2_000_000_000, 4_000_000_000u, long.MinValue)));

    key = CompositeKey.Create(ulong.MaxValue, 'Ж', 1.5f, -2.25, 79228162514264337593543950335m, time, guid, "zone\0tree");
    var (unsigned, character, single, floating, number, readTime, readGuid, text) =
        CompositeKey.Decode<ulong, char, float, double, decimal, DateTime, Guid, string>(key);
    Assert.That(unsigned, Is.EqualTo(ulong.MaxValue));
    Assert.That(character, Is.EqualTo('Ж'));
    Assert.That(single, Is.EqualTo(1.5f));
    Assert.That(floating, Is.EqualTo(-2.25));
    Assert.That(number, Is.EqualTo(decimal.MaxValue));
    Assert.That(readTime.Ticks, Is.EqualTo(time.Ticks));
    Assert.That(readTime.Kind, Is.EqualTo(DateTimeKind.Unspecified));
    Assert.That(readGuid, Is.EqualTo(guid));
    Assert.That(text, Is.EqualTo("zone\0tree"));

    byte[] bytes = [0, 1, 0, 255, 0];
    key = CompositeKey.Create(bytes, new Memory<byte>(bytes), (string)null, (byte[])null, (int?)null, (Guid?)guid, (decimal?)-0.001m);
    Assert.That(CompositeKey.Get<byte[]>(key, 0), Is.EqualTo(bytes));
    Assert.That(CompositeKey.Get<Memory<byte>>(key, 1).ToArray(), Is.EqualTo(bytes));
    Assert.That(CompositeKey.Get<string>(key, 2), Is.Null);
    Assert.That(CompositeKey.Get<byte[]>(key, 3), Is.Null);
    Assert.That(CompositeKey.Get<long?>(key, 4), Is.Null);
    Assert.That(CompositeKey.Get<Guid?>(key, 5), Is.EqualTo(guid));
    Assert.That(CompositeKey.Get<decimal?>(key, 6), Is.EqualTo(-0.001m));
    Assert.That(CompositeKey.Get<decimal>(key, 6), Is.EqualTo(-0.001m));

    // Descending elements read back as their own types.
    key = CompositeKey.Create(CompositeKey.Descending("zone\0tree"), CompositeKey.Descending(-42L),
        CompositeKey.Descending(-12.5m), CompositeKey.Descending<int?>(null), CompositeKey.Descending(bytes),
        CompositeKey.Descending(time), CompositeKey.Descending(guid), CompositeKey.Descending(double.NaN));
    Assert.That(CompositeKey.Decode<string, long, decimal, int?, byte[], DateTime, Guid, double>(key),
        Is.EqualTo(("zone\0tree", -42L, -12.5m, (int?)null, bytes, new DateTime(time.Ticks), guid, double.NaN)));
    for (var i = 0; i < 8; ++i)
      Assert.That(CompositeKey.IsDescending(key, i), Is.True);
  }

  [Test]
  public void LongStringsAndBytesWithZerosReadBack()
  {
    var text = string.Concat(Enumerable.Range(0, 400).Select(i => i % 7 == 0 ? "\0" : ((char)('a' + i % 26)).ToString()));
    var bytes = Enumerable.Range(0, 600).Select(i => (byte)(i % 5 == 0 ? 0 : i)).ToArray();
    var key = CompositeKey.Create(text, bytes, CompositeKey.Descending(text), CompositeKey.Descending(bytes));
    Assert.That(CompositeKey.Decode<string, byte[], string, byte[]>(key), Is.EqualTo((text, bytes, text, bytes)));
    Assert.That(CompositeKey.IsValid(key), Is.True);
  }

  [Test]
  public void IntegersOfEveryTypeAreOneTypeComparedByValue()
  {
    var five = CompositeKey.Create(5L);
    Assert.That(CompositeKey.Create((byte)5), Is.EqualTo(five));
    Assert.That(CompositeKey.Create((sbyte)5), Is.EqualTo(five));
    Assert.That(CompositeKey.Create((ushort)5), Is.EqualTo(five));
    Assert.That(CompositeKey.Create(5u), Is.EqualTo(five));
    Assert.That(CompositeKey.Create(5UL), Is.EqualTo(five));
    Assert.That(CompositeKey.Create((char)5), Is.EqualTo(five));
    Assert.That(CompositeKey.Create((int?)5), Is.EqualTo(five));
    Assert.That(CompositeKey.Get<byte>(five, 0), Is.EqualTo(5));
    Assert.That(CompositeKey.ElementType(five, 0), Is.EqualTo(CompositeElementType.Integer));

    // A float is a floating value as a double is.
    Assert.That(CompositeKey.Create(1.5f), Is.EqualTo(CompositeKey.Create(1.5)));
    Assert.That(CompositeKey.Get<float>(CompositeKey.Create(1.5), 0), Is.EqualTo(1.5f));

    // Reading a value a type cannot hold exactly fails.
    Assert.Throws<OverflowException>(() => CompositeKey.Get<byte>(CompositeKey.Create(300), 0));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<uint>(CompositeKey.Create(-1), 0));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<long>(CompositeKey.Create(ulong.MaxValue), 0));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<sbyte>(CompositeKey.Create(-129), 0));
    Assert.That(CompositeKey.Get<sbyte>(CompositeKey.Create(-128), 0), Is.EqualTo(sbyte.MinValue));
    Assert.That(CompositeKey.Get<long>(CompositeKey.Create(long.MinValue), 0), Is.EqualTo(long.MinValue));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<float>(CompositeKey.Create(0.1), 0));
  }

  [Test]
  public void WideIntegersSortWithEveryOtherInteger()
  {
    AssertSorted<BigInteger>(
        -BigInteger.Pow(2, 2039), -BigInteger.Pow(2, 200), -BigInteger.Pow(2, 128), Int128.MinValue,
        -BigInteger.Pow(2, 64) - 1, -BigInteger.Pow(2, 64), -(BigInteger)ulong.MaxValue, (BigInteger)long.MinValue - 1,
        long.MinValue, -1, 0, 1, long.MaxValue, ulong.MaxValue, BigInteger.Pow(2, 64), Int128.MaxValue,
        UInt128.MaxValue, BigInteger.Pow(2, 128), BigInteger.Pow(2, 2039));

    // An integer holds at most 255 bytes.
    var largest = BigInteger.Pow(2, 2040) - 1;
    Assert.That(CompositeKey.Get<BigInteger>(CompositeKey.Create(largest), 0), Is.EqualTo(largest));
    Assert.That(CompositeKey.Get<BigInteger>(CompositeKey.Create(-largest), 0), Is.EqualTo(-largest));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeKey.Create(largest + 1));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeKey.Create(-largest - 1));

    // Every integer type writes the same bytes for the same value.
    Assert.That(CompositeKey.Create((Int128)5), Is.EqualTo(CompositeKey.Create(5)));
    Assert.That(CompositeKey.Create(new BigInteger(-300)), Is.EqualTo(CompositeKey.Create(-300)));
    Assert.That(CompositeKey.Create((Int128)ulong.MaxValue + 1), Is.EqualTo(CompositeKey.Create(BigInteger.Pow(2, 64))));
    Assert.That(CompositeKey.Create((UInt128)ulong.MaxValue + 1), Is.EqualTo(CompositeKey.Create(BigInteger.Pow(2, 64))));
    Assert.That(CompositeKey.Create(Int128.MinValue), Is.EqualTo(CompositeKey.Create(-BigInteger.Pow(2, 127))));
    Assert.That(CompositeKey.Create((nint)(-7)), Is.EqualTo(CompositeKey.Create(-7)));
    Assert.That(CompositeKey.Create((nuint)7), Is.EqualTo(CompositeKey.Create(7)));

    // Reading checks the range of the type read.
    Assert.That(CompositeKey.Get<Int128>(CompositeKey.Create(Int128.MinValue), 0), Is.EqualTo(Int128.MinValue));
    Assert.That(CompositeKey.Get<Int128>(CompositeKey.Create(Int128.MaxValue), 0), Is.EqualTo(Int128.MaxValue));
    Assert.That(CompositeKey.Get<UInt128>(CompositeKey.Create(UInt128.MaxValue), 0), Is.EqualTo(UInt128.MaxValue));
    Assert.That(CompositeKey.Get<UInt128>(CompositeKey.Create(5), 0), Is.EqualTo((UInt128)5));
    Assert.That(CompositeKey.Get<nint>(CompositeKey.Create(-7), 0), Is.EqualTo((nint)(-7)));
    Assert.That(CompositeKey.Get<nuint>(CompositeKey.Create(7), 0), Is.EqualTo((nuint)7));
    Assert.That(CompositeKey.Get<long>(CompositeKey.Create((BigInteger)long.MinValue), 0), Is.EqualTo(long.MinValue));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<Int128>(CompositeKey.Create(UInt128.MaxValue), 0));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<Int128>(CompositeKey.Create(-BigInteger.Pow(2, 127) - 1), 0));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<UInt128>(CompositeKey.Create(-1), 0));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<UInt128>(CompositeKey.Create(BigInteger.Pow(2, 128)), 0));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<long>(CompositeKey.Create(BigInteger.Pow(2, 64)), 0));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<long>(CompositeKey.Create((BigInteger)long.MinValue - 1), 0));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<ulong>(CompositeKey.Create(-(BigInteger)ulong.MaxValue), 0));
  }

  enum Status { Draft = -1, Open = 0, Closed = 7 }

  enum Wide : ulong { Low = 1, High = ulong.MaxValue }

  enum Small : byte { A = 1, B = 200 }

  [Test]
  public void EnumsAreIntegersOfTheirUnderlyingValues()
  {
    Assert.That(CompositeKey.Create(Status.Closed), Is.EqualTo(CompositeKey.Create(7)));
    Assert.That(CompositeKey.Create(Status.Draft), Is.EqualTo(CompositeKey.Create(-1)));
    Assert.That(CompositeKey.Create(Wide.High), Is.EqualTo(CompositeKey.Create(ulong.MaxValue)));
    Assert.That(CompositeKey.Create(Small.B), Is.EqualTo(CompositeKey.Create(200)));
    var key = CompositeKey.Create(Status.Draft, (Status?)Status.Closed, (Status?)null, CompositeKey.Descending(Wide.High),
        CompositeKey.Descending<Small?>(Small.B), Small.A);
    Assert.That(CompositeKey.Decode<Status, Status?, Status?, Wide, Small?, Small>(key),
        Is.EqualTo((Status.Draft, (Status?)Status.Closed, (Status?)null, Wide.High, (Small?)Small.B, Small.A)));
    Assert.That(CompositeKey.ElementType(key, 0), Is.EqualTo(CompositeElementType.Integer));
    Assert.That(CompositeKey.Get<long>(key, 1), Is.EqualTo(7));
    AssertSorted(Status.Draft, Status.Open, Status.Closed);
    AssertSorted(Wide.Low, Wide.High);

    // An enum reads any integer its underlying type holds, named or not.
    Assert.That(CompositeKey.Get<Status>(CompositeKey.Create(42), 0), Is.EqualTo((Status)42));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<Small>(CompositeKey.Create(300), 0));
    Assert.Throws<OverflowException>(() => CompositeKey.Get<Wide>(CompositeKey.Create(-1), 0));
  }

  [Test]
  public void TimesReadBackAndSortByValue()
  {
    var instant = new DateTimeOffset(2026, 11, 15, 12, 30, 0, TimeSpan.FromHours(3));
    var date = new DateOnly(2026, 11, 15);
    var time = new TimeOnly(23, 59, 59, 999);
    var interval = TimeSpan.FromTicks(-123456789);
    var key = CompositeKey.Create(instant, date, time, interval, (DateTimeOffset?)null, (DateOnly?)date,
        CompositeKey.Descending(time), CompositeKey.Descending(interval));
    var (readInstant, readDate, readTime, readInterval, noInstant, someDate, descendingTime, descendingInterval) =
        CompositeKey.Decode<DateTimeOffset, DateOnly, TimeOnly, TimeSpan, DateTimeOffset?, DateOnly?, TimeOnly, TimeSpan>(key);
    Assert.That(readInstant.UtcTicks, Is.EqualTo(instant.UtcTicks));
    Assert.That(readInstant.Offset, Is.EqualTo(TimeSpan.Zero));
    Assert.That(readDate, Is.EqualTo(date));
    Assert.That(readTime, Is.EqualTo(time));
    Assert.That(readInterval, Is.EqualTo(interval));
    Assert.That(noInstant, Is.Null);
    Assert.That(someDate, Is.EqualTo(date));
    Assert.That(descendingTime, Is.EqualTo(time));
    Assert.That(descendingInterval, Is.EqualTo(interval));
    CompositeElementType[] types =
    [
      CompositeElementType.DateTimeOffset, CompositeElementType.DateOnly, CompositeElementType.TimeOnly,
      CompositeElementType.TimeSpan
    ];
    for (var i = 0; i < types.Length; ++i)
      Assert.That(CompositeKey.ElementType(key, i), Is.EqualTo(types[i]));

    // The same instant at any offset is one element.
    Assert.That(CompositeKey.Create(instant), Is.EqualTo(CompositeKey.Create(instant.ToUniversalTime())));
    Assert.That(CompositeKey.Create(instant), Is.EqualTo(CompositeKey.Create(instant.ToOffset(TimeSpan.FromHours(-8)))));

    AssertSorted(DateTimeOffset.MinValue, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(5)),
        new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue);
    AssertSorted(DateOnly.MinValue, new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 15), DateOnly.MaxValue);
    AssertSorted(TimeOnly.MinValue, new TimeOnly(0, 0, 1), new TimeOnly(12, 0), TimeOnly.MaxValue);
    AssertSorted(TimeSpan.MinValue, TimeSpan.FromDays(-1), TimeSpan.FromTicks(-1), TimeSpan.Zero, TimeSpan.FromTicks(1),
        TimeSpan.MaxValue);

    // Each time type is a type of its own.
    Assert.Throws<InvalidCastException>(() => CompositeKey.Get<DateTime>(CompositeKey.Create(instant), 0));
    Assert.Throws<InvalidCastException>(() => CompositeKey.Get<TimeSpan>(CompositeKey.Create(time), 0));
    Assert.Throws<InvalidCastException>(() => CompositeKey.Get<DateOnly>(CompositeKey.Create(DateTime.MinValue), 0));
  }

  [Test]
  public void HalvesAndReadOnlyBytesJoinTheirTypes()
  {
    Assert.That(CompositeKey.Create((Half)1.5), Is.EqualTo(CompositeKey.Create(1.5)));
    Assert.That(CompositeKey.Create(Half.NaN), Is.EqualTo(CompositeKey.Create(double.NaN)));
    Assert.That(CompositeKey.Get<Half>(CompositeKey.Create(0.25), 0), Is.EqualTo((Half)0.25));
    Assert.That(Half.IsNaN(CompositeKey.Get<Half>(CompositeKey.Create(double.NaN), 0)), Is.True);
    Assert.That(CompositeKey.Get<Half?>(CompositeKey.Create((Half?)null), 0), Is.Null);
    Assert.Throws<OverflowException>(() => CompositeKey.Get<Half>(CompositeKey.Create(0.1), 0));
    AssertSorted(Half.NegativeInfinity, (Half)(-1), Half.Zero, Half.Epsilon, (Half)1, Half.MaxValue, Half.PositiveInfinity, Half.NaN);

    byte[] bytes = [0, 7, 0];
    var key = CompositeKey.Create(new ReadOnlyMemory<byte>(bytes), (ReadOnlyMemory<byte>?)null, CompositeKey.Descending(new ReadOnlyMemory<byte>(bytes)));
    Assert.That(key, Is.EqualTo(CompositeKey.Create(bytes, (byte[])null, CompositeKey.Descending(bytes))));
    Assert.That(CompositeKey.Get<ReadOnlyMemory<byte>>(key, 0).ToArray(), Is.EqualTo(bytes));
    Assert.That(CompositeKey.Get<ReadOnlyMemory<byte>?>(key, 1), Is.Null);
    Assert.That(CompositeKey.Get<ReadOnlyMemory<byte>>(key, 2).ToArray(), Is.EqualTo(bytes));
  }

  [Test]
  public void EveryValueHasOneEncoding()
  {
    Assert.That(CompositeKey.Create(-0.0), Is.EqualTo(CompositeKey.Create(0.0)));
    Assert.That(CompositeKey.Create(BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0001))),
        Is.EqualTo(CompositeKey.Create(double.NaN)));
    Assert.That(CompositeKey.Create(1.0m), Is.EqualTo(CompositeKey.Create(1.00m)));
    Assert.That(CompositeKey.Create(1.0m), Is.EqualTo(CompositeKey.Create(1m)));
    Assert.That(CompositeKey.Create(100m), Is.EqualTo(CompositeKey.Create(100.000m)));
    Assert.That(CompositeKey.Create(-0.0m), Is.EqualTo(CompositeKey.Create(0m)));
    Assert.That(CompositeKey.Get<decimal>(CompositeKey.Create(1.500m), 0).ToString(CultureInfo.InvariantCulture), Is.EqualTo("1.5"));
    var local = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
    Assert.That(CompositeKey.Create(local), Is.EqualTo(CompositeKey.Create(new DateTime(local.Ticks, DateTimeKind.Utc))));
  }

  [Test]
  public void ValuesOfEachTypeSortAsTheirBytes()
  {
    AssertSorted(
        long.MinValue, long.MinValue + 1, -(1L << 32) - 1, -(1L << 32), -65536, -65535, -256, -255, -2, -1,
        0, 1, 255, 256, 65535, 65536, 1L << 40, long.MaxValue);
    AssertSorted((ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue);
    AssertSorted(
        double.NegativeInfinity, double.MinValue, -1e100, -1.5, -1, -double.Epsilon, 0.0, double.Epsilon, 1e-300,
        0.5, 1, 1.5, 1e100, double.MaxValue, double.PositiveInfinity, double.NaN);
    AssertSorted(
        decimal.MinValue, -1e20m, -100m, -99.9m, -10m, -1.5m, -1.25m, -1m, -0.5m, -0.123m, -0.12m, -0.1m,
        -0.0000000000000000000000000001m, 0m, 0.0000000000000000000000000001m, 0.1m, 0.12m, 0.123m, 0.13m, 1m,
        1.0000000000000000000000000001m, 1.5m, 9.9m, 10m, 99m, 100m, 1e20m, decimal.MaxValue);
    AssertSorted("", "\0", "\0\0", "\0a", "a", "a\0", "a\0b", "ab", "b", "é", "", "￿", "\U0001F600");
    AssertSorted<byte[]>([], [0], [0, 0], [0, 1], [0, 255], [1], [1, 0], [255], [255, 255]);
    AssertSorted(false, true);
    AssertSorted(DateTime.MinValue, new DateTime(2000, 1, 1), new DateTime(2000, 1, 1, 0, 0, 1), DateTime.MaxValue);
    var guids = Enumerable.Range(0, 50).Select(i => Guid.NewGuid()).OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToArray();
    AssertSorted(guids);
  }

  /// <summary>Checks that values encode in the given order ascending, and in the reverse order descending.</summary>
  static void AssertSorted<T>(params T[] values)
  {
    for (var i = 0; i < values.Length; ++i)
    {
      for (var j = 0; j < values.Length; ++j)
      {
        var expected = Math.Sign(i.CompareTo(j));
        Assert.That(Math.Sign(Order.Compare(CompositeKey.Create(values[i]), CompositeKey.Create(values[j]))), Is.EqualTo(expected),
            $"{values[i]} and {values[j]}");
        Assert.That(Math.Sign(Order.Compare(CompositeKey.Create(CompositeKey.Descending(values[i])),
            CompositeKey.Create(CompositeKey.Descending(values[j])))), Is.EqualTo(-expected), $"descending {values[i]} and {values[j]}");
      }
    }
  }

  [Test]
  public void TypesSortInTheirOrderAscendingBeforeDescending()
  {
    byte[][] ordered =
    [
      CompositeKey.Create((string)null),
      CompositeKey.Create(new byte[] { 255 }),
      CompositeKey.Create(""),
      CompositeKey.Create(-BigInteger.Pow(2, 2000)),
      CompositeKey.Create(long.MinValue),
      CompositeKey.Create(ulong.MaxValue),
      CompositeKey.Create(BigInteger.Pow(2, 2000)),
      CompositeKey.Create(double.NegativeInfinity),
      CompositeKey.Create(decimal.MinValue),
      CompositeKey.Create(false),
      CompositeKey.Create(DateTime.MinValue),
      CompositeKey.Create(DateTimeOffset.MinValue),
      CompositeKey.Create(DateOnly.MinValue),
      CompositeKey.Create(TimeOnly.MinValue),
      CompositeKey.Create(TimeSpan.MinValue),
      CompositeKey.Create(Guid.Empty),
      CompositeKey.Create(CompositeKey.Descending(Guid.Empty)),
      CompositeKey.Create(CompositeKey.Descending(long.MinValue)),
      CompositeKey.Create(CompositeKey.Descending<string>(null)),
    ];
    AssertOrdered(ordered);
  }

  [Test]
  public void KeysSortElementByElementWithPrefixesFirst()
  {
    byte[][] ordered =
    [
      [],
      CompositeKey.Create("a"),
      CompositeKey.Create("a", (string)null),
      CompositeKey.Create("a", ""),
      CompositeKey.Create("a", "x"),
      CompositeKey.Create("a", 1),
      CompositeKey.Create("a", 1, 1),
      CompositeKey.Create("a", 2),
      CompositeKey.Create("a", CompositeKey.Descending(2)),
      CompositeKey.Create("a", CompositeKey.Descending(1)),
      CompositeKey.Create("a\0"),
      CompositeKey.Create("a\0", 1),
      CompositeKey.Create("ab"),
      CompositeKey.Create(CompositeKey.Descending("b")),
      CompositeKey.Create(CompositeKey.Descending("ab")),
      CompositeKey.Create(CompositeKey.Descending("a\0")),
      CompositeKey.Create(CompositeKey.Descending("a")),
      CompositeKey.Create(CompositeKey.Descending("a"), 1),
      CompositeKey.Create(CompositeKey.Descending("")),
    ];
    AssertOrdered(ordered);
  }

  static void AssertOrdered(byte[][] ordered)
  {
    for (var i = 0; i < ordered.Length; ++i)
    {
      for (var j = 0; j < ordered.Length; ++j)
        Assert.That(Math.Sign(Order.Compare(ordered[i], ordered[j])), Is.EqualTo(Math.Sign(i.CompareTo(j))), $"keys {i} and {j}");
    }
  }

  [Test]
  public void KeysJoinEndToEnd()
  {
    var key = CompositeKey.Create("orders", 42L, CompositeKey.Descending(1.5), (string)null);
    Assert.That(Join(CompositeKey.Create("orders"), CompositeKey.Create(42L), CompositeKey.Create(CompositeKey.Descending(1.5)),
        CompositeKey.Create((string)null)), Is.EqualTo(key));
    Assert.That(Join(CompositeKey.Create("orders", 42L), CompositeKey.Create(CompositeKey.Descending(1.5), (string)null)), Is.EqualTo(key));
    Assert.That(Join(CompositeKey.ElementBytes(key, 0).ToArray(), CompositeKey.ElementBytes(key, 1).ToArray(),
        CompositeKey.ElementBytes(key, 2).ToArray(), CompositeKey.ElementBytes(key, 3).ToArray()), Is.EqualTo(key));
    Assert.That(CompositeKey.ElementBytes(key, 1, 2).ToArray(), Is.EqualTo(CompositeKey.Create(42L, CompositeKey.Descending(1.5))));
    Assert.That(CompositeKey.ElementBytes(key, 0, 4).ToArray(), Is.EqualTo(key));
    Assert.That(CompositeKey.ElementBytes(key, 2, 0).IsEmpty, Is.True);
    Assert.That(CompositeKey.ElementBytes(key, 4, 0).IsEmpty, Is.True);
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeKey.ElementBytes(key, 3, 2));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeKey.ElementBytes(key, 5, 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeKey.ElementBytes(key, -1, 1));

    // An index entry takes the row's key as it is, and gives it back.
    var row = CompositeKey.Create("row", 42L);
    byte[] entry = [.. CompositeKey.Create("by-customer", "bob"), .. CompositeKey.ElementBytes(row, 1)];
    Assert.That(entry, Is.EqualTo(CompositeKey.Create("by-customer", "bob", 42L)));
    Assert.That(CompositeKey.Get<long>(entry, 2), Is.EqualTo(42L));
  }

  static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

  [Test]
  public void RangeHoldsTheKeysUnderWholeElements()
  {
    object[][] tuples =
    [
      ["by-customer"], ["by-customer", "bob"], ["by-customer", "bob", 1L], ["by-customer", "bob", 2L],
      ["by-customer", "bob\0", 1L], ["by-customer", "bobby", 1L], ["by-customer", "bo"], ["by-customer", "alice", 1L],
      ["by-customer", null], ["by-customer\0"], ["by-customers"], ["by-time"], ["row", 1L], [], ["a"],
    ];
    var keys = tuples.Select(EncodeAscending).ToList();
    foreach (var prefixTuple in tuples)
    {
      var range = CompositeKey.Range(EncodeAscending(prefixTuple));
      for (var i = 0; i < tuples.Length; ++i)
      {
        var inRange = range.Contains(keys[i]);
        var isUnder = tuples[i].Length >= prefixTuple.Length && tuples[i].Take(prefixTuple.Length).SequenceEqual(prefixTuple);
        Assert.That(inRange, Is.EqualTo(isUnder), $"[{string.Join(", ", tuples[i])}] for [{string.Join(", ", prefixTuple)}]");
      }
    }

    // A descending prefix holds its extensions and nothing that only starts with its bytes.
    var descending = CompositeKey.Range(CompositeKey.Create(CompositeKey.Descending("a")));
    bool In(byte[] key) => descending.Contains(key);
    Assert.That(In(CompositeKey.Create(CompositeKey.Descending("a"))), Is.True);
    Assert.That(In(CompositeKey.Create(CompositeKey.Descending("a"), 7)), Is.True);
    Assert.That(In(CompositeKey.Create(CompositeKey.Descending("a"), CompositeKey.Descending((string)null))), Is.True);
    Assert.That(In(CompositeKey.Create(CompositeKey.Descending("a\0"))), Is.False);
    Assert.That(In(CompositeKey.Create(CompositeKey.Descending("ab"))), Is.False);
    Assert.That(In(CompositeKey.Create(CompositeKey.Descending(""))), Is.False);

    Assert.Throws<ArgumentException>(() => CompositeKey.Range([0x03, 0x61]));
  }

  static byte[] EncodeAscending(object[] tuple)
      => Join(tuple.Select(element => element switch
      {
        null => CompositeKey.Create((string)null),
        string text => CompositeKey.Create(text),
        long number => CompositeKey.Create(number),
        _ => throw new ArgumentException(element.GetType().Name)
      }).ToArray());

  [TestCase(CompositeKeyOrder.Ascending)]
  [TestCase(CompositeKeyOrder.Descending)]
  public void BeginsWithHoldsTheStringsThatBeginWithText(CompositeKeyOrder order)
  {
    string[] names = ["", "a", "b", "bn", "bo", "bo\0", "bo\0x", "boa", "bob", "bobby", "bo\U0001F600", "bp", "bé", "c", "￿"];
    byte[] Key(string name) => order == CompositeKeyOrder.Ascending
        ? CompositeKey.Create("by-name", name, 1L)
        : CompositeKey.Create("by-name", CompositeKey.Descending(name), 1L);
    var keys = names.Select(Key).ToList();
    // Keys of other key spaces, and an integer where the string would be.
    keys.Add(CompositeKey.Create("by-namf", "bo"));
    keys.Add(CompositeKey.Create("by-name\0", "bo"));
    keys.Add(CompositeKey.Create("by-name", 5));
    foreach (var text in new[] { "bo", "b", "", "bo\0", "bob", "z" })
    {
      var range = CompositeKey.BeginsWith(CompositeKey.Create("by-name"), text, order);
      for (var i = 0; i < keys.Count; ++i)
      {
        var inRange = range.Contains(keys[i]);
        var beginsWith = i < names.Length && names[i].StartsWith(text, StringComparison.Ordinal);
        Assert.That(inRange, Is.EqualTo(beginsWith), $"{(i < names.Length ? Show(names[i]) : $"key {i}")} for {Show(text)}");
      }
    }

    // A string the keys start with, with no prefix.
    var bare = CompositeKey.BeginsWith([], "bo", order);
    var bob = order == CompositeKeyOrder.Ascending ? CompositeKey.Create("bob") : CompositeKey.Create(CompositeKey.Descending("bob"));
    Assert.That(bare.Contains(bob), Is.True);
  }

  [TestCase(CompositeKeyOrder.Ascending)]
  [TestCase(CompositeKeyOrder.Descending)]
  public void BeginsWithHoldsTheBytesElementsThatBeginWithBytes(CompositeKeyOrder order)
  {
    byte[][] values = [[], [0], [0, 0], [0, 1], [0, 1, 0], [0, 1, 255], [0, 2], [1], [255]];
    byte[] Key(byte[] value) => order == CompositeKeyOrder.Ascending
        ? CompositeKey.Create("hash", value)
        : CompositeKey.Create("hash", CompositeKey.Descending(value));
    foreach (var start in new byte[][] { [0, 1], [0], [], [255] })
    {
      var range = CompositeKey.BeginsWith(CompositeKey.Create("hash"), start, order);
      foreach (var value in values)
      {
        var key = Key(value);
        var inRange = range.Contains(key);
        Assert.That(inRange, Is.EqualTo(value.AsSpan().StartsWith(start)), $"{Convert.ToHexString(value)} for {Convert.ToHexString(start)}");
      }
    }
    Assert.Throws<ArgumentException>(() => CompositeKey.BeginsWith([0x03, 0x61], [1], order));
  }

  [Test]
  public void BeginsWithChecksItsArguments()
  {
    Assert.Throws<ArgumentException>(() => CompositeKey.BeginsWith([0x03, 0x61], "bo"));
    Assert.Throws<ArgumentException>(() => CompositeKey.BeginsWith([], "\uD800"));
    Assert.Throws<ArgumentNullException>(() => CompositeKey.BeginsWith([], (string)null));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeKey.BeginsWith([], "bo", (CompositeKeyOrder)5));
    var longText = new string('x', 400);
    var range = CompositeKey.BeginsWith([], longText);
    Assert.That(range.Contains(CompositeKey.Create(longText + "y")), Is.True);
  }

  [Test]
  public void DecodeReadsTheLeadingElements()
  {
    var key = CompositeKey.Create("acme", 42L, new DateTime(2026, 11, 15), 1.5f);
    Assert.That(CompositeKey.Decode<string>(key), Is.EqualTo("acme"));
    Assert.That(CompositeKey.Decode<string, long>(key), Is.EqualTo(("acme", 42L)));
    Assert.That(CompositeKey.Decode<string, long, DateTime, float>(key), Is.EqualTo(("acme", 42L, new DateTime(2026, 11, 15), 1.5f)));
    Assert.That(CompositeKey.Decode<string, int, DateTime>(key), Is.EqualTo(("acme", 42, new DateTime(2026, 11, 15))));

    var tooFew = Assert.Throws<ArgumentException>(() => CompositeKey.Decode<string, long, DateTime, float, int>(key));
    Assert.That(tooFew.Message, Does.Contain("4 elements, fewer than 5"));
    Assert.Throws<ArgumentException>(() => CompositeKey.Decode<string>([]));
    Assert.Throws<InvalidCastException>(() => CompositeKey.Decode<string, DateTime>(key));
    Assert.Throws<InvalidCastException>(() => CompositeKey.Get<long>(CompositeKey.Create((string)null), 0));
    Assert.Throws<InvalidCastException>(() => CompositeKey.Get<bool>(CompositeKey.Create(1), 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeKey.Get<string>(key, 4));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeKey.Get<string>(key, -1));
    Assert.Throws<NotSupportedException>(() => CompositeKey.Get<Descending<string>>(key, 0));
    Assert.Throws<NotSupportedException>(() => CompositeKey.Get<object>(key, 0));
    Assert.Throws<NotSupportedException>(() => CompositeKey.Create(new object()));
    Assert.Throws<NotSupportedException>(() => CompositeKey.Create(CompositeKey.Descending(CompositeKey.Descending(1))));
    Assert.Throws<NotSupportedException>(() => CompositeKey.Create(new Version(1, 2)));
    Assert.Throws<ArgumentException>(() => CompositeKey.Create("\uD800"));
  }

  [Test]
  public void TryReadsReturnFalseWhereReadsThrow()
  {
    var placed = new DateTime(2026, 11, 15);
    var key = CompositeKey.Create("acme", 300L, placed, 1.0 / 3);
    Assert.That(CompositeKey.TryDecode(key, out string name), Is.True);
    Assert.That(name, Is.EqualTo("acme"));
    Assert.That(CompositeKey.TryDecode(key, out (string, int, DateTime) three), Is.True);
    Assert.That(three, Is.EqualTo(("acme", 300, placed)));
    Assert.That(CompositeKey.TryGet(key, 3, out double third), Is.True);
    Assert.That(third, Is.EqualTo(1.0 / 3));
    Assert.That(CompositeKey.TryGet(CompositeKey.Create((string)null), 0, out string nothing), Is.True);
    Assert.That(nothing, Is.Null);

    // Each failure leaves the default behind.
    Assert.That(CompositeKey.TryDecode(key, out (string, long, DateTime, double, int) tooFew), Is.False);
    Assert.That(tooFew, Is.EqualTo(default((string, long, DateTime, double, int))));
    Assert.That(CompositeKey.TryDecode(key, out (string, DateTime) mismatch), Is.False);
    Assert.That(mismatch, Is.EqualTo(default((string, DateTime))));
    Assert.That(CompositeKey.TryGet(key, 1, out byte overflow), Is.False);
    Assert.That(overflow, Is.Zero);
    Assert.That(CompositeKey.TryGet(key, 3, out float _), Is.False);
    Assert.That(CompositeKey.TryGet(key, 4, out string _), Is.False);
    Assert.That(CompositeKey.TryGet(key, -1, out string _), Is.False);
    Assert.That(CompositeKey.TryDecode<string>([], out _), Is.False);
    Assert.That(CompositeKey.TryGet(CompositeKey.Create((string)null), 0, out long _), Is.False);

    // Bytes that stop being a key fail where a read reaches them, and a string of invalid UTF-8 as it is read.
    byte[] broken = [.. CompositeKey.Create("a"), 0xFF];
    Assert.That(CompositeKey.TryGet(broken, 0, out string first), Is.True);
    Assert.That(first, Is.EqualTo("a"));
    Assert.That(CompositeKey.TryGet(broken, 1, out string _), Is.False);
    Assert.That(CompositeKey.TryDecode(broken, out (string, string) _), Is.False);
    byte[] invalidUtf8 = [0x03, 0xC3, 0x28, 0x00, 0x01];
    Assert.That(CompositeKey.TryGet(invalidUtf8, 0, out string _), Is.False);
    Assert.Throws<ArgumentException>(() => CompositeKey.Get<string>(invalidUtf8, 0));

    // A type no element is, is a mistake in the code, so it still throws.
    Assert.Throws<NotSupportedException>(() => CompositeKey.TryGet<object>([], 0, out _));
    Assert.Throws<NotSupportedException>(() => CompositeKey.TryDecode<string, Descending<int>>(key, out _));

    var reader = new CompositeKeyReader(key);
    Assert.That(reader.Next(), Is.True);
    Assert.That(reader.TryRead(out int wrong), Is.False);
    Assert.That(wrong, Is.Zero);
    Assert.That(reader.TryRead(out string text), Is.True);
    Assert.That(text, Is.EqualTo("acme"));
    var notOnElement = false;
    try
    {
      new CompositeKeyReader(key).TryRead(out string _);
    }
    catch (InvalidOperationException)
    {
      notOnElement = true;
    }
    Assert.That(notOnElement, Is.True);

    var eight = CompositeKey.Create(1, 2, 3, 4, 5, 6, 7, 8);
    Assert.That(CompositeKey.TryDecode(eight, out (int, int, int, int) four), Is.True);
    Assert.That(four, Is.EqualTo((1, 2, 3, 4)));
    Assert.That(CompositeKey.TryDecode(eight, out (int, int, int, int, int) five), Is.True);
    Assert.That(five, Is.EqualTo((1, 2, 3, 4, 5)));
    Assert.That(CompositeKey.TryDecode(eight, out (int, int, int, int, int, int) six), Is.True);
    Assert.That(six, Is.EqualTo((1, 2, 3, 4, 5, 6)));
    Assert.That(CompositeKey.TryDecode(eight, out (int, int, int, int, int, int, int) seven), Is.True);
    Assert.That(seven, Is.EqualTo((1, 2, 3, 4, 5, 6, 7)));
    Assert.That(CompositeKey.TryDecode(eight, out (int, int, int, int, int, int, int, int) all), Is.True);
    Assert.That(all, Is.EqualTo((1, 2, 3, 4, 5, 6, 7, 8)));
    Assert.That(CompositeKey.TryDecode(eight, out (int, int, int, int, int, int, int, string) _), Is.False);
  }

  [Test]
  public void BetweenHoldsTheKeysFromOnePrefixThroughAnother()
  {
    var monday = new DateOnly(2026, 11, 9);
    foreach (var descending in new[] { false, true })
    {
      byte[] Day(int day) => descending
          ? CompositeKey.Create("bob", CompositeKey.Descending(monday.AddDays(day)))
          : CompositeKey.Create("bob", monday.AddDays(day));
      byte[] Key(string customer, int day) => descending
          ? CompositeKey.Create(customer, CompositeKey.Descending(monday.AddDays(day)), 1L)
          : CompositeKey.Create(customer, monday.AddDays(day), 1L);

      // Monday through Friday, whichever order the days come in and whichever way they sort.
      var bounds = CompositeKey.Between(Day(0), Day(4));
      Assert.That(CompositeKey.Between(Day(4), Day(0)), Is.EqualTo(bounds));
      for (var day = -1; day <= 5; ++day)
        Assert.That(Holds(bounds, Key("bob", day)), Is.EqualTo(day is >= 0 and <= 4), $"day {day}, descending {descending}");
      Assert.That(Holds(bounds, Day(0)), Is.True);
      Assert.That(Holds(bounds, Day(4)), Is.True);
      Assert.That(Holds(bounds, Key("bobby", 1)), Is.False);
      Assert.That(Holds(bounds, Key("alice", 1)), Is.False);
    }

    // One prefix twice bounds what Range bounds.
    var bob = CompositeKey.Create("bob");
    Assert.That(CompositeKey.Between(bob, bob), Is.EqualTo(CompositeKey.Range(bob)));
    Assert.Throws<ArgumentException>(() => CompositeKey.Between([0x03, 0x61], bob));
    Assert.Throws<ArgumentException>(() => CompositeKey.Between(bob, [0xFF]));
  }

  [Test]
  public void BetweenIncludesOrExcludesEachValueAsItsBoundsSay()
  {
    (CompositeKeyBounds Bounds, long Low, long High)[] cases =
    [
      (CompositeKeyBounds.Inclusive, 2, 4), (CompositeKeyBounds.ExcludeLow, 3, 4),
      (CompositeKeyBounds.ExcludeHigh, 2, 3), (CompositeKeyBounds.Exclusive, 3, 3),
    ];
    foreach (var descending in new[] { false, true })
    {
      // Bob's keys of ids 1 to 5: each id's prefix is a key itself, and has a key under it.
      byte[] Id(long id) => descending ? CompositeKey.Create("bob", CompositeKey.Descending(id)) : CompositeKey.Create("bob", id);
      byte[] Under(long id) => descending
          ? CompositeKey.Create("bob", CompositeKey.Descending(id), "row")
          : CompositeKey.Create("bob", id, "row");
      foreach (var (bounds, low, high) in cases)
      {
        var range = CompositeKey.Between(Id(2), Id(4), bounds);
        // The upper bound is never a key, even where it excludes a value whose prefix is a key itself.
        Assert.That(CompositeKey.IsValid(range.Upper), Is.False, $"{bounds}, descending {descending}");
        for (var id = 1L; id <= 5; ++id)
        {
          var held = id >= low && id <= high;
          Assert.That(Holds(range, Id(id)), Is.EqualTo(held), $"{bounds}, id {id}, descending {descending}");
          Assert.That(Holds(range, Under(id)), Is.EqualTo(held), $"{bounds}, under id {id}, descending {descending}");
          Assert.That(range.Contains(Id(id)), Is.EqualTo(held), $"{bounds}, contains id {id}, descending {descending}");
        }
      }

      // The bounds name the prefix they exclude by its argument, whichever sorts first.
      Assert.That(CompositeKey.Between(Id(4), Id(2), CompositeKeyBounds.ExcludeLow),
          Is.EqualTo(CompositeKey.Between(Id(2), Id(4), CompositeKeyBounds.ExcludeHigh)));
      // An excluded value on both ends of one value leaves nothing.
      var empty = CompositeKey.Between(Id(3), Id(3), CompositeKeyBounds.ExcludeHigh);
      Assert.That(Holds(empty, Id(3)) || Holds(empty, Under(3)), Is.False);
    }
    var bob = CompositeKey.Create("bob");
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeKey.Between(bob, bob, (CompositeKeyBounds)4));
  }

  /// <summary>
  /// Whether a range holds a key, by two whole comparisons: what <see cref="CompositeKeyRange.Contains"/> must answer.
  /// </summary>
  static bool Holds(CompositeKeyRange range, byte[] key)
      => Order.Compare(key, range.Lower) >= 0 && Order.Compare(key, range.Upper) < 0;

  [Test]
  public void TheBoundBelowExcludedElementsHasNoKeyBetween()
  {
    var (elements, keys, prefix) = EdgeKeys();
    // Every element type is among them, in both orders, so a type added later fails here until it is.
    var covered = elements.Select(element => (CompositeKey.ElementType(element, 0), CompositeKey.IsDescending(element, 0))).ToHashSet();
    foreach (var type in Enum.GetValues<CompositeElementType>())
    {
      Assert.That(covered, Does.Contain((type, false)), $"{type}");
      Assert.That(covered, Does.Contain((type, true)), $"{type}, descending");
    }

    var failures = new List<string>();
    foreach (var element in elements)
      CheckBelow([.. prefix, .. element], keys, failures);
    Assert.That(failures, Is.Empty);
    Assert.That(CompositeKeyFormat.Below([]), Is.Empty);
  }

  [Test]
  public void ContainsHoldsExactlyTheKeysAtOrAboveLowerAndBelowUpper()
  {
    // Every pair of short bounds, equal, one starting the other, or in either order, over every short key around
    // them. Ranges of composite keys are checked by CheckRange.
    var bounds = AllBytes(2, [0x00, 0x01, 0x02, 0xFF]);
    var keys = AllBytes(3, [0x00, 0x01, 0x02, 0x03, 0xFF]);
    var failures = new List<string>();
    foreach (var lower in bounds)
    {
      foreach (var upper in bounds)
      {
        var range = new CompositeKeyRange(lower, upper);
        foreach (var key in keys)
        {
          var holds = Holds(range, key);
          if (range.Contains(key) != holds)
            failures.Add($"{range} contains 0x{Convert.ToHexString(key)}: {!holds}");
        }
      }
    }
    Assert.That(failures, Is.Empty);
  }

  /// <summary>Every byte array of the given bytes, up to a length.</summary>
  static List<byte[]> AllBytes(int maxLength, byte[] alphabet)
  {
    List<byte[]> all = [[]];
    var start = 0;
    for (var length = 1; length <= maxLength; ++length)
    {
      var end = all.Count;
      for (var i = start; i < end; ++i)
      {
        foreach (var b in alphabet)
          all.Add([.. all[i], b]);
      }
      start = end;
    }
    return all;
  }

  [Test]
  public void EveryRangeEndsAtNoKeyAndScansTheSameKeysBothWays()
  {
    var (elements, keys, prefix) = EdgeKeys();
    CompositeKeyBounds[] everyBounds =
        [CompositeKeyBounds.Inclusive, CompositeKeyBounds.ExcludeLow, CompositeKeyBounds.ExcludeHigh, CompositeKeyBounds.Exclusive];
    var ranges = new List<(string Name, CompositeKeyRange Range)>
    {
      ("Range([])", CompositeKey.Range([])), ($"Range({CompositeKey.Format(prefix)})", CompositeKey.Range(prefix)),
    };
    foreach (var bounds in everyBounds)
      ranges.Add(($"Between([], [], {bounds})", CompositeKey.Between([], [], bounds)));
    for (var i = 0; i < elements.Count; ++i)
    {
      byte[] low = [.. prefix, .. elements[i]];
      byte[] high = [.. prefix, .. elements[(i + 1) % elements.Count]];
      ranges.Add(($"Range({CompositeKey.Format(low)})", CompositeKey.Range(low)));
      foreach (var bounds in everyBounds)
        ranges.Add(($"Between({CompositeKey.Format(low)}, {CompositeKey.Format(high)}, {bounds})", CompositeKey.Between(low, high, bounds)));
    }
    foreach (var start in new[] { Array.Empty<byte>(), prefix })
    {
      foreach (var order in new[] { CompositeKeyOrder.Ascending, CompositeKeyOrder.Descending })
      {
        foreach (var text in new[] { "", "\0", "a", "bo", "\U0010FFFF" })
          ranges.Add(($"BeginsWith({CompositeKey.Format(start)}, \"{text}\", {order})", CompositeKey.BeginsWith(start, text, order)));
        foreach (var bytes in new byte[][] { [], [0], [0, 0xFF], [0x7F], [0xFF], [0xFF, 0xFF] })
          ranges.Add(($"BeginsWith({CompositeKey.Format(start)}, 0x{Convert.ToHexString(bytes)}, {order})", CompositeKey.BeginsWith(start, bytes, order)));
      }
    }

    var sorted = Sorted(keys);
    var failures = new List<string>();
    foreach (var (name, range) in ranges)
      CheckRange(name, range, sorted, failures);
    Assert.That(failures, Is.Empty);
  }

  [Test]
  public void RandomRangesEndAtNoKeyAndScanTheSameKeysBothWays()
  {
    CompositeKeyBounds[] everyBounds =
        [CompositeKeyBounds.Inclusive, CompositeKeyBounds.ExcludeLow, CompositeKeyBounds.ExcludeHigh, CompositeKeyBounds.Exclusive];
    byte[][] prefixes = [[], CompositeKey.Create("q"), CompositeKey.Create("q", 1)];
    var failures = new List<string>();
    Span<int> offsets = stackalloc int[16];
    for (var seed = 0; seed < 300 && failures.Count == 0; ++seed)
    {
      var random = new Random(seed);
      // Random keys, with every whole-element prefix of each, so the prefixes ranges start and end at are keys too.
      var keys = new List<byte[]>();
      for (var i = 0; i < 60; ++i)
      {
        var builder = new CompositeKeyBuilder().AddElements(prefixes[random.Next(prefixes.Length)]);
        for (var count = random.Next(1, 4); count > 0; --count)
          builder.AddElements(RandomElement(random));
        var key = builder.ToArray();
        var elementCount = CompositeKey.Offsets(key, offsets);
        for (var e = 0; e <= elementCount; ++e)
          keys.Add(key[..offsets[e]]);
      }
      var sorted = Sorted(keys);
      // Often one of the prefixes, the empty key among them, since most ranges start or end at one.
      byte[] Any() => random.Next(4) == 0 ? prefixes[random.Next(prefixes.Length)] : sorted[random.Next(sorted.Count)];

      var ranges = new List<(string Name, CompositeKeyRange Range)>();
      var sides = new List<(string Name, CompositeKeyRange Range, byte[] Bound, Func<int, bool> Side)>();
      for (var i = 0; i < 10; ++i)
      {
        var at = Any();
        ranges.Add(($"Range({CompositeKey.Format(at)})", CompositeKey.Range(at)));
        // Between values of one element after shared leading ones, as most ranges are, and between any two keys.
        var bounds = everyBounds[random.Next(everyBounds.Length)];
        byte[] low = [.. at, .. RandomElement(random)];
        byte[] high = [.. at, .. RandomElement(random)];
        ranges.Add(($"Between({CompositeKey.Format(low)}, {CompositeKey.Format(high)}, {bounds})", CompositeKey.Between(low, high, bounds)));
        var (one, other) = (Any(), Any());
        ranges.Add(($"Between({CompositeKey.Format(one)}, {CompositeKey.Format(other)}, {bounds})", CompositeKey.Between(one, other, bounds)));
        var (sideName, side, holds) = Sides[random.Next(Sides.Length)];
        sides.Add(($"{sideName}({CompositeKey.Format(low)})", side(low), low, holds));
        var order = random.Next(2) == 0 ? CompositeKeyOrder.Ascending : CompositeKeyOrder.Descending;
        var text = RandomText(random);
        ranges.Add(($"BeginsWith({CompositeKey.Format(at)}, text of {text.Length}, {order})", CompositeKey.BeginsWith(at, text, order)));
        var bytes = RandomBytes(random);
        ranges.Add(($"BeginsWith({CompositeKey.Format(at)}, 0x{Convert.ToHexString(bytes)}, {order})", CompositeKey.BeginsWith(at, bytes, order)));
      }
      foreach (var (name, range) in ranges)
        CheckRange($"seed {seed}: {name}", range, sorted, failures);
      foreach (var (name, range, bound, holds) in sides)
        CheckSide($"seed {seed}: {name}", range, bound, holds, sorted, failures);
      // Every range read a page at a time, and continued after and before a key.
      foreach (var (name, range) in ranges.Concat(sides.Select(side => (side.Name, side.Range))))
      {
        CheckPages($"seed {seed}: {name}", range, random.Next(1, 4), sorted, failures);
        CheckContinued($"seed {seed}: {name}", range, Any(), sorted, failures);
      }
      foreach (var excluded in sorted)
        if (excluded.Length > 0)
          CheckBelow(excluded, sorted, failures, $"seed {seed}: ");
    }
    Assert.That(failures, Is.Empty);
  }

  [Test]
  public void OneSidedRangesHoldTheValuesOnTheirSideOfTheBound()
  {
    var (elements, keys, prefix) = EdgeKeys();
    var sorted = Sorted(keys);
    var failures = new List<string>();
    foreach (var element in elements)
    {
      foreach (var start in new[] { Array.Empty<byte>(), prefix })
      {
        byte[] bound = [.. start, .. element];
        foreach (var (name, side, holds) in Sides)
          CheckSide($"{name}({CompositeKey.Format(bound)})", side(bound), bound, holds, sorted, failures);
      }
    }

    // Every key is under the empty end, the empty key among them: including its keys holds every key, excluding none.
    (string Name, CompositeKeyRange Range, Func<byte[], bool> Holds)[] empty =
    [
      ("AtLeast([])", CompositeKey.AtLeast([]), key => true),
      ("GreaterThan([])", CompositeKey.GreaterThan([]), key => false),
      ("AtMost([])", CompositeKey.AtMost([]), key => true),
      ("LessThan([])", CompositeKey.LessThan([]), key => false),
    ];
    Assert.That(sorted[0], Is.Empty);
    foreach (var (name, range, holds) in empty)
    {
      CheckRange(name, range, sorted, failures);
      foreach (var key in sorted)
      {
        if (range.Contains(key) != holds(key))
          failures.Add($"{name} holds {CompositeKey.Format(key)}: {!holds(key)}");
      }
    }
    Assert.That(failures, Is.Empty);
    Assert.That(CompositeKey.AtLeast([]), Is.EqualTo(CompositeKey.Range([])));
    Assert.That(CompositeKey.AtMost([]), Is.EqualTo(CompositeKey.Range([])));
    Assert.Throws<ArgumentException>(() => CompositeKey.LessThan([0x03, 0x61]));
  }

  [Test]
  public void TheSidesOfAnEndAgreeWithEachOtherAndWithItsRange()
  {
    var (elements, keys, prefix) = EdgeKeys();
    var sorted = Sorted(keys);
    var failures = new List<string>();
    List<byte[]> ends = [[]];
    foreach (var element in elements)
    {
      ends.Add(element);
      ends.Add([.. prefix, .. element]);
    }
    foreach (var end in ends)
    {
      var (atLeast, greaterThan) = (CompositeKey.AtLeast(end), CompositeKey.GreaterThan(end));
      var (atMost, lessThan) = (CompositeKey.AtMost(end), CompositeKey.LessThan(end));
      var range = CompositeKey.Range(end);
      // The keys a one-sided range of the end can hold: those with an element after its leading ones, or every key.
      var head = end.Length == 0 ? [] : end.AsSpan(0, CompositeKeyFormat.LastElement(end)).ToArray();
      foreach (var key in sorted)
      {
        var inScope = end.Length == 0 || key.AsSpan().StartsWith(head) && key.Length > head.Length;
        var (a, g, m, l) = (atLeast.Contains(key), greaterThan.Contains(key), atMost.Contains(key), lessThan.Contains(key));
        // At least and at most share exactly the end's own keys, greater and less share none, and each side and its
        // opposite together hold every key in scope, once.
        if ((a && m) != range.Contains(key) || (g && l) || (a ^ l) != inScope || (g ^ m) != inScope)
          failures.Add($"{CompositeKey.Format(end)} at {CompositeKey.Format(key)}: " +
              $"at least {a}, greater {g}, at most {m}, less {l}");
      }
    }
    Assert.That(failures, Is.Empty);
  }

  /// <summary>Each one-sided range, and how its element's value must compare with the bound's to be in it.</summary>
  static readonly (string Name, Func<byte[], CompositeKeyRange> Side, Func<int, bool> Holds)[] Sides =
  [
    ("AtLeast", bound => CompositeKey.AtLeast(bound), order => order >= 0),
    ("GreaterThan", bound => CompositeKey.GreaterThan(bound), order => order > 0),
    ("AtMost", bound => CompositeKey.AtMost(bound), order => order <= 0),
    ("LessThan", bound => CompositeKey.LessThan(bound), order => order < 0),
  ];

  /// <summary>
  /// Checks that a one-sided range is a range as <see cref="CheckRange"/> checks, which holds exactly what a Between
  /// with that one end holds: the keys that start with the bound's leading elements and go on with an element of any
  /// type that compares with the bound's last as the side says, as keys sort, and in reverse for a descending bound.
  /// </summary>
  static void CheckSide(
      string name, CompositeKeyRange range, byte[] bound, Func<int, bool> holds, List<byte[]> sorted, List<string> failures)
  {
    CheckRange(name, range, sorted, failures);
    var position = CompositeKey.ElementCount(bound) - 1;
    var head = bound.AsSpan(0, CompositeKeyFormat.LastElement(bound));
    var descending = CompositeKey.IsDescending(bound, position);
    foreach (var key in sorted)
    {
      var expected = false;
      if (key.AsSpan().StartsWith(head) && CompositeKey.ElementCount(key) > position)
      {
        // Elements compare as keys sort, and values of a descending bound's order in reverse.
        var order = Math.Sign(CompositeKey.ElementBytes(key, position).SequenceCompareTo(CompositeKey.ElementBytes(bound, position)));
        expected = holds(descending ? -order : order);
      }
      if (range.Contains(key) != expected)
        failures.Add($"{name} holds {CompositeKey.Format(key)}: {!expected}");
    }
  }

  /// <summary>
  /// Checks that a range read a page at a time reads each of its keys once, in order: forward, each page continuing
  /// after the last key of the page before, and backward, each continuing before the first.
  /// </summary>
  static void CheckPages(string name, CompositeKeyRange range, int size, List<byte[]> sorted, List<string> failures)
  {
    var all = ScanUp(sorted, range);
    var forward = new List<byte[]>();
    var page = range;
    for (var pages = 0; pages <= all.Count; ++pages)
    {
      var keys = ScanUp(sorted, page, size);
      forward.AddRange(keys);
      if (keys.Count == 0)
        break;
      page = page.StartAfter(keys[^1]);
    }
    var backward = new List<byte[]>();
    page = range;
    for (var pages = 0; pages <= all.Count; ++pages)
    {
      var keys = ScanDown(sorted, page, size);
      backward.AddRange(keys);
      if (keys.Count == 0)
        break;
      page = page.EndBefore(keys[^1]);
    }
    backward.Reverse();
    if (!forward.SequenceEqual(all, new ByteArrayEquality()))
      failures.Add($"{name} reads {forward.Count} keys for its {all.Count} in pages of {size} forward");
    if (!backward.SequenceEqual(all, new ByteArrayEquality()))
      failures.Add($"{name} reads {backward.Count} keys for its {all.Count} in pages of {size} backward");
  }

  /// <summary>
  /// Checks that a range continued after a key, and one continued before it, are ranges as
  /// <see cref="CheckRange"/> checks, which hold exactly the range's keys above the key, and below it.
  /// </summary>
  static void CheckContinued(string name, CompositeKeyRange range, byte[] key, List<byte[]> sorted, List<string> failures)
  {
    var after = range.StartAfter(key);
    var before = range.EndBefore(key);
    CheckRange($"{name}.StartAfter({CompositeKey.Format(key)})", after, sorted, failures);
    CheckRange($"{name}.EndBefore({CompositeKey.Format(key)})", before, sorted, failures);
    foreach (var other in sorted)
    {
      var order = other.AsSpan().SequenceCompareTo(key);
      if (after.Contains(other) != (Holds(range, other) && order > 0))
        failures.Add($"{name}.StartAfter({CompositeKey.Format(key)}) holds {CompositeKey.Format(other)}: {after.Contains(other)}");
      if (before.Contains(other) != (Holds(range, other) && order < 0))
        failures.Add($"{name}.EndBefore({CompositeKey.Format(key)}) holds {CompositeKey.Format(other)}: {before.Contains(other)}");
    }
  }

  /// <summary>
  /// Checks that a range ends at no key, that it contains exactly the keys it holds, and that both of its scans read
  /// them all.
  /// </summary>
  static void CheckRange(string name, CompositeKeyRange range, List<byte[]> sorted, List<string> failures)
  {
    if (CompositeKey.IsValid(range.Upper))
      failures.Add($"{name} ends at a key, {CompositeKey.Format(range.Upper)}");
    var held = 0;
    foreach (var key in sorted)
    {
      var holds = Holds(range, key);
      if (range.Contains(key) != holds)
        failures.Add($"{name} contains {CompositeKey.Format(key)}: {!holds}");
      if (holds)
        ++held;
    }
    var up = ScanUp(sorted, range).Count;
    var down = ScanDown(sorted, range).Count;
    if (up != held)
      failures.Add($"{name} scans {up} of its {held} keys upward");
    if (down != held)
      failures.Add($"{name} scans {down} of its {held} keys downward");
  }

  /// <summary>
  /// The keys a forward iterator reads of a range, up to a limit: from the first key at or above its lower bound,
  /// as ZoneTreeIterator's seek lands, while the range contains them.
  /// </summary>
  static List<byte[]> ScanUp(List<byte[]> sorted, CompositeKeyRange range, int limit = int.MaxValue)
  {
    var keys = new List<byte[]>();
    for (var i = CountBelow(sorted, range.Lower, orAt: false); i < sorted.Count && keys.Count < limit && range.Contains(sorted[i]); ++i)
      keys.Add(sorted[i]);
    return keys;
  }

  /// <summary>
  /// The keys a reverse iterator reads of a range, up to a limit: from the last key at or below its upper bound, as
  /// ZoneTreeIterator's seek lands, while the range contains them.
  /// </summary>
  static List<byte[]> ScanDown(List<byte[]> sorted, CompositeKeyRange range, int limit = int.MaxValue)
  {
    var keys = new List<byte[]>();
    for (var i = CountBelow(sorted, range.Upper, orAt: true) - 1; i >= 0 && keys.Count < limit && range.Contains(sorted[i]); --i)
      keys.Add(sorted[i]);
    return keys;
  }

  /// <summary>How many of the sorted keys sort below bytes, or at or below them.</summary>
  static int CountBelow(List<byte[]> sorted, byte[] bytes, bool orAt)
  {
    var (low, high) = (0, sorted.Count);
    while (low < high)
    {
      var middle = (low + high) / 2;
      var order = sorted[middle].AsSpan().SequenceCompareTo(bytes);
      if (order < 0 || orAt && order == 0)
        low = middle + 1;
      else
        high = middle;
    }
    return low;
  }

  /// <summary>Checks that the bound below elements is no key, sorts below them, and has no key between it and them.</summary>
  static void CheckBelow(byte[] excluded, List<byte[]> keys, List<string> failures, string context = "")
  {
    var below = CompositeKeyFormat.Below(excluded);
    if (CompositeKey.IsValid(below))
      failures.Add($"{context}the bound below {CompositeKey.Format(excluded)} is a key");
    if (below.AsSpan().SequenceCompareTo(excluded) >= 0)
      failures.Add($"{context}the bound below {CompositeKey.Format(excluded)} does not sort below it");
    foreach (var key in keys)
    {
      // A key sorts below the bound exactly when it sorts below the excluded elements: none lies between.
      var belowExcluded = key.AsSpan().SequenceCompareTo(excluded) < 0;
      if (belowExcluded != key.AsSpan().SequenceCompareTo(below) < 0)
        failures.Add($"{context}{CompositeKey.Format(key)} lies between the bound below {CompositeKey.Format(excluded)} and it");
    }
  }

  static List<byte[]> Sorted(IEnumerable<byte[]> keys)
  {
    var sorted = keys.Distinct(new ByteArrayEquality()).ToList();
    sorted.Sort((x, y) => x.AsSpan().SequenceCompareTo(y));
    return sorted;
  }

  /// <summary>An element of a random type and order, its value often at an edge of its type.</summary>
  static byte[] RandomElement(Random random)
  {
    var descending = random.Next(2) == 1;
    byte[] Of<T>(T value) => descending ? CompositeKey.Create(CompositeKey.Descending(value)) : CompositeKey.Create(value);
    switch (random.Next(14))
    {
      case 0:
        return Of((string)null);
      case 1:
        return Of(random.Next(2) == 1);
      case 2:
        return Of(RandomLong(random));
      case 3:
        var magnitude = new byte[random.Next(9, 33)];
        random.NextBytes(magnitude);
        if (random.Next(3) == 0)
          magnitude.AsSpan().Fill(0xFF);
        magnitude[0] |= 1;
        var big = new BigInteger(magnitude, isUnsigned: true, isBigEndian: true);
        return Of(random.Next(2) == 0 ? big : -big);
      case 4:
        return Of(random.Next(3) switch
        {
          0 => new[] { double.NegativeInfinity, double.PositiveInfinity, double.NaN, 0.0, -double.Epsilon, double.Epsilon, double.MaxValue, double.MinValue }[random.Next(8)],
          1 => random.Next(-5, 5) / 2.0,
          _ => BitConverter.Int64BitsToDouble(random.NextInt64(long.MinValue, long.MaxValue)),
        });
      case 5:
        var negative = random.Next(2) == 1;
        if (random.Next(4) == 0)
        {
          int Part() => random.Next(3) switch { 0 => 0, 1 => -1, _ => random.Next(int.MinValue, int.MaxValue) };
          return Of(new decimal(Part(), Part(), Part(), negative, (byte)random.Next(29)));
        }
        // Digits from few pieces, so that values such as 1.5 and 1.55 share leading digits, and their bytes a prefix.
        var digitCount = random.Next(1, 6);
        var digits = 0;
        for (var d = 0; d < digitCount; ++d)
          digits = digits * 10 + random.Next(4) switch { 0 => 0, 1 => 1, 2 => 5, _ => 9 };
        return Of(new decimal(digits, 0, 0, negative, (byte)random.Next(digitCount + 1)));
      case 6:
        return Of(new DateTime(RandomTicks(random, DateTime.MaxValue.Ticks)));
      case 7:
        var utc = RandomTicks(random, DateTime.MaxValue.Ticks);
        var minutes = random.Next(-840, 841);
        var local = utc + minutes * TimeSpan.TicksPerMinute;
        if (local < 0 || local > DateTime.MaxValue.Ticks)
          (local, minutes) = (utc, 0);
        return Of(new DateTimeOffset(local, TimeSpan.FromMinutes(minutes)));
      case 8:
        return Of(DateOnly.FromDayNumber((int)RandomTicks(random, DateOnly.MaxValue.DayNumber)));
      case 9:
        return Of(new TimeOnly(RandomTicks(random, TimeOnly.MaxValue.Ticks)));
      case 10:
        return Of(new TimeSpan(RandomLong(random)));
      case 11:
        var guid = new byte[16];
        random.NextBytes(guid);
        if (random.Next(3) == 0)
          guid.AsSpan().Fill(random.Next(2) == 0 ? (byte)0 : (byte)0xFF);
        return Of(new Guid(guid));
      case 12:
        return Of(RandomText(random));
      default:
        return Of(RandomBytes(random));
    }
  }

  static long RandomLong(Random random) => random.Next(4) switch
  {
    0 => new[] { long.MinValue, long.MaxValue, 0, 1, -1, 255, -255, 256, -256, 65535, -65535 }[random.Next(11)],
    1 => random.Next(-300, 300),
    2 => random.NextInt64(long.MinValue, long.MaxValue) >> random.Next(64),
    _ => random.NextInt64(long.MinValue, long.MaxValue),
  };

  static long RandomTicks(Random random, long maximum) => random.Next(3) switch
  {
    0 => 0,
    1 => maximum,
    _ => random.NextInt64(0, maximum + 1),
  };

  static string RandomText(Random random)
  {
    string[] pieces = ["\0", "\u0001", "a", "b", "\u00e9", "\uFFFF", "\U0010FFFF", "\U0001F600"];
    var text = new StringBuilder();
    for (var count = random.Next(5); count > 0; --count)
      text.Append(pieces[random.Next(pieces.Length)]);
    return text.ToString();
  }

  static byte[] RandomBytes(Random random)
  {
    byte[] pieces = [0x00, 0x01, 0x7F, 0xFE, 0xFF];
    var bytes = new byte[random.Next(5)];
    for (var i = 0; i < bytes.Length; ++i)
      bytes[i] = random.Next(3) == 0 ? (byte)random.Next(256) : pieces[random.Next(pieces.Length)];
    return bytes;
  }

  sealed class ByteArrayEquality : IEqualityComparer<byte[]>
  {
    public bool Equals(byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj)
    {
      var hash = new HashCode();
      hash.AddBytes(obj);
      return hash.ToHashCode();
    }
  }

  /// <summary>
  /// Elements of every type at its edges, ascending and descending, and keys of each after a prefix, alone and
  /// followed by elements that sort high, as a key just below another would; and keys outside the prefix.
  /// </summary>
  static (List<byte[]> Elements, List<byte[]> Keys, byte[] Prefix) EdgeKeys()
  {
    var elements = new List<byte[]>();
    void Add<T>(params T[] values)
    {
      foreach (var value in values)
      {
        elements.Add(CompositeKey.Create(value));
        elements.Add(CompositeKey.Create(CompositeKey.Descending(value)));
      }
    }
    Add<string>(null, "", "\0", "\0\0", "a", "a\0", "b", "bob", "\uFFFF", "\U0010FFFF");
    Add<byte[]>([], [0], [0, 0], [1], [0x7F], [0xFF], [0xFF, 0xFF]);
    Add(false, true);
    Add(long.MinValue, -(1L << 56), -65536L, -65535L, -256L, -255L, -2L, -1L, 0L, 1L, 2L, 255L, 256L, 1L << 56, long.MaxValue);
    Add(ulong.MaxValue);
    var wide = BigInteger.Pow(2, 64);
    var allOnes = (BigInteger.One << 72) - 1;
    Add(wide, -wide, allOnes, -allOnes, -(wide - 1), BigInteger.Pow(2, 2039), -BigInteger.Pow(2, 2039));
    Add(double.NegativeInfinity, -1.5, -double.Epsilon, 0.0, double.Epsilon, 1.5, double.MaxValue, double.PositiveInfinity, double.NaN);
    Add(decimal.MinValue, -1.5m, -1m, -0.0001m, 0m, 0.0001m, 1m, 1.5m, 10m, decimal.MaxValue);
    Add(DateTime.MinValue, new DateTime(2026, 11, 15), DateTime.MaxValue);
    Add(DateTimeOffset.MinValue, new DateTimeOffset(2026, 11, 15, 9, 30, 0, TimeSpan.FromHours(3)), DateTimeOffset.MaxValue);
    Add(DateOnly.MinValue, new DateOnly(2026, 11, 15), DateOnly.MaxValue);
    Add(TimeOnly.MinValue, new TimeOnly(9, 30), TimeOnly.MaxValue);
    Add(TimeSpan.MinValue, TimeSpan.FromTicks(-1), TimeSpan.Zero, TimeSpan.MaxValue);
    Add(Guid.Empty, Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));

    // Keys of each element alone and followed by elements that sort high, as a key just below another would.
    byte[][] rests =
    [
      [], CompositeKey.Create(new byte[] { 0xFF, 0xFF, 0xFF }), CompositeKey.Create(CompositeKey.Descending(Array.Empty<byte>())),
      CompositeKey.Create("\U0010FFFF"), CompositeKey.Create(CompositeKey.Descending(DateTime.MinValue)),
      CompositeKey.Create(CompositeKey.Descending((string)null)), CompositeKey.Create(long.MaxValue),
    ];
    var prefix = CompositeKey.Create("q");
    List<byte[]> keys = [[], prefix, CompositeKey.Create("p", 1), CompositeKey.Create("r")];
    foreach (var element in elements)
    {
      foreach (var rest in rests)
        keys.Add([.. prefix, .. element, .. rest]);
    }
    return (elements, keys, prefix);
  }

  [Test]
  public void ARangeHoldsTheKeysFromItsLowerBoundUpToItsUpperBound()
  {
    var range = new CompositeKeyRange([2], [4]);
    Assert.That(range.Contains([1]), Is.False);
    Assert.That(range.Contains([2]), Is.True);
    Assert.That(range.Contains([3, 9]), Is.True);
    Assert.That(range.Contains([4]), Is.False);
    var (lower, upper) = range;
    Assert.That(lower, Is.EqualTo(new byte[] { 2 }));
    Assert.That(upper, Is.EqualTo(new byte[] { 4 }));
    // Ranges are equal when their bounds are, byte for byte.
    Assert.That(range, Is.EqualTo(new CompositeKeyRange([2], [4])));
    Assert.That(range == new CompositeKeyRange([2], [4]), Is.True);
    Assert.That(range != new CompositeKeyRange([2], [5]), Is.True);
    Assert.That(range.GetHashCode(), Is.EqualTo(new CompositeKeyRange([2], [4]).GetHashCode()));
    Assert.That(range.ToString(), Is.EqualTo("[0x02, 0x04)"));
    Assert.Throws<ArgumentNullException>(() => new CompositeKeyRange(null, [1]));
    Assert.Throws<ArgumentNullException>(() => new CompositeKeyRange([1], null));

    // Continuing a range moves one bound inward, never outward.
    Assert.That(range.StartAfter([3]), Is.EqualTo(new CompositeKeyRange([3, 0], [4])));
    Assert.That(range.StartAfter([1]), Is.EqualTo(range));
    var every = CompositeKey.Range([]);
    Assert.That(every.EndBefore(CompositeKey.Create(5)).Contains(CompositeKey.Create(4)), Is.True);
    Assert.That(every.EndBefore(CompositeKey.Create(5)).Contains(CompositeKey.Create(5)), Is.False);
    Assert.That(every.EndBefore([]).Contains([]), Is.False);
    Assert.Throws<ArgumentException>(() => every.EndBefore([0x03, 0x61]));
  }

  [Test]
  public void FormatShowsEveryElementAsText()
  {
    Assert.That(CompositeKey.Format(CompositeKey.Create("by-customer", "bob", 42L)), Is.EqualTo("""("by-customer", "bob", 42)"""));
    var key = new CompositeKeyBuilder()
        .Add((string)null)
        .Add(new byte[] { 0, 0xAB })
        .Add("say \"hi\"\\\n\0\u0001ğ")
        .Add(-7)
        .Add(BigInteger.Pow(2, 100))
        .Add(2.0)
        .Add(1.5f)
        .Add(double.NaN)
        .Add(1.00m)
        .Add(true)
        .Add(new DateTime(2026, 11, 15, 9, 30, 0, DateTimeKind.Utc))
        .Add(new DateTimeOffset(2026, 11, 15, 9, 30, 0, TimeSpan.FromHours(3)))
        .Add(new DateOnly(2026, 11, 15))
        .Add(new TimeOnly(9, 30))
        .Add(TimeSpan.FromHours(-1.5))
        .Add(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"))
        .Add(CompositeKey.Descending("ab"))
        .ToArray();
    var text = """
        (null, 0x00AB, "say \"hi\"\\\n\0\u0001ğ", -7, 1267650600228229401496703205376, 2.0, 1.5, NaN, 1m, true, 2026-11-15T09:30:00.0000000, 2026-11-15T06:30:00.0000000+00:00, 2026-11-15, T09:30:00.0000000, -01:30:00, 0f8fad5b-d9cb-469f-a165-70867728950e, desc("ab"))
        """;
    Assert.That(CompositeKey.Format(key), Is.EqualTo(text));

    // The text is the same in every culture.
    var culture = CultureInfo.CurrentCulture;
    try
    {
      CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
      Assert.That(CompositeKey.Format(key), Is.EqualTo(text));
    }
    finally
    {
      CultureInfo.CurrentCulture = culture;
    }

    // Any bytes have a text: those that are not a key show from where they stop being one.
    Assert.That(CompositeKey.Format([]), Is.EqualTo("()"));
    Assert.That(CompositeKey.Format([0xFF]), Is.EqualTo("(malformed(0xFF))"));
    Assert.That(CompositeKey.Format([.. CompositeKey.Create("a"), 0xFF, 0x01]), Is.EqualTo("""("a", malformed(0xFF01))"""));
    Assert.That(CompositeKey.Format([.. CompositeKey.Create(1), 0x03, 0xC3, 0x28, 0x00, 0x01]),
        Is.EqualTo("(1, malformed(0x03C3280001))"));
  }

  [Test]
  public void ElementsAreDescribedWithoutTheirTypes()
  {
    var key = CompositeKey.Create((string)null, new byte[] { 1 }, "text", -7, 2.5, 3.25m, true,
        CompositeKey.Descending(new DateTime(2026, 1, 1)));
    key = Join(key, CompositeKey.Create(Guid.NewGuid()));
    CompositeElementType[] types =
    [
      CompositeElementType.Null, CompositeElementType.Bytes, CompositeElementType.String,
      CompositeElementType.Integer, CompositeElementType.Floating, CompositeElementType.Decimal,
      CompositeElementType.Boolean, CompositeElementType.DateTime, CompositeElementType.Guid
    ];
    Assert.That(CompositeKey.ElementCount(key), Is.EqualTo(types.Length));
    Assert.That(CompositeKey.ElementCount([]), Is.Zero);

    Span<int> offsets = stackalloc int[16];
    var count = CompositeKey.Offsets(key, offsets);
    Assert.That(count, Is.EqualTo(types.Length));
    Assert.That(offsets[count], Is.EqualTo(key.Length));

    var reader = new CompositeKeyReader(key);
    for (var i = 0; i < types.Length; ++i)
    {
      Assert.That(reader.Next(), Is.True);
      Assert.That(reader.ElementType, Is.EqualTo(types[i]));
      Assert.That(CompositeKey.ElementType(key, i), Is.EqualTo(types[i]));
      Assert.That(reader.IsDescending, Is.EqualTo(i == 7));
      Assert.That(CompositeKey.IsDescending(key, i), Is.EqualTo(i == 7));
      Assert.That(reader.Offset, Is.EqualTo(offsets[i]));
      Assert.That(reader.ElementBytes.ToArray(), Is.EqualTo(key[offsets[i]..offsets[i + 1]]));
      Assert.That(CompositeKey.ElementBytes(key, i).ToArray(), Is.EqualTo(key[offsets[i]..offsets[i + 1]]));
    }
    Assert.That(reader.Read<Guid>(), Is.EqualTo(CompositeKey.Get<Guid>(key, 8)));
    Assert.That(reader.Next(), Is.False);
    Assert.That(reader.Next(), Is.False);
    var after = new CompositeKeyReader(key);
    after.Next();
    Assert.That(after.Read<string>(), Is.Null);
    Assert.Throws<InvalidOperationException>(() => new CompositeKeyReader(key).Read<string>());

    Assert.Throws<ArgumentException>(() => CompositeKey.Offsets(key, new int[types.Length]));
    Assert.Throws<ArgumentException>(() => CompositeKey.Offsets(key, []));
    Assert.That(CompositeKey.Offsets([], new int[1]), Is.Zero);
  }

  [Test]
  public void IsValidTellsCompositeKeysFromMalformedBytes()
  {
    byte[][] malformed =
    [
      // Reserved and unknown tags.
      [0x00],
      [0xFF],
      [0x23],
      [0x7F],
      // Integers: a missing byte, a leading zero, a negative zero, and wide ones that fit 8 bytes, lack their
      // length, are cut short or start with a zero byte.
      [0x0E],
      [0x0F, 0x00, 0x05],
      [0x0C, 0xFF],
      [0x16, 0x08, 1, 2, 3, 4, 5, 6, 7, 8],
      [0x16],
      [0x16, 0x09, 0x01],
      [0x16, 0x09, 0x00, 1, 2, 3, 4, 5, 6, 7, 8],
      [0x04, 0xF7, 1, 2, 3, 4, 5, 6, 7, 8],
      [0x04, 0xF6, 0xFF, 1, 2, 3, 4, 5, 6, 7, 8],
      // A negative zero and a NaN of another payload.
      [0x17, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
      [0x17, 0xFF, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01],
      // Times past their ranges, and cut short.
      [0x1D, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
      [0x1E, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
      [0x1F, 0xFF, 0xFF, 0xFF, 0xFF],
      [0x20, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
      [0x21, 0x01],
      [0x22, 0x01, 0x02],
      // Bytes without their terminator, and a zero byte without its escape.
      [0x02, 0x61],
      [0x02, 0x61, 0x00],
      [0x02, 0x00, 0x05, 0x00, 0x01],
      // Decimals: a trailing zero digit, a leading zero digit, no digits, a byte that is no digit, no terminator,
      // an exponent out of range, and a value too large.
      [0x1A, 0x81, 0x31, 0x30, 0x00],
      [0x1A, 0x81, 0x30, 0x31, 0x00],
      [0x1A, 0x81, 0x00],
      [0x1A, 0x81, 0x3A, 0x00],
      [0x1A, 0x81, 0x31],
      [0x1A, 0x9E, 0x31, 0x00],
      [0x1A, 0x9D, 0x38, 0x00],
      [.. CompositeKey.Create("valid"), 0x0E],
    ];
    foreach (var bytes in malformed)
    {
      Assert.That(CompositeKey.IsValid(bytes), Is.False, Convert.ToHexString(bytes));
      Assert.Throws<ArgumentException>(() => CompositeKey.ElementCount(bytes), Convert.ToHexString(bytes));
    }

    // A string's UTF-8 is checked when it is read.
    byte[] invalidUtf8 = [0x03, 0xC3, 0x28, 0x00, 0x01];
    Assert.That(CompositeKey.IsValid(invalidUtf8), Is.False);
    Assert.That(CompositeKey.ElementCount(invalidUtf8), Is.EqualTo(1));
    Assert.Throws<ArgumentException>(() => CompositeKey.Get<string>(invalidUtf8, 0));

    Assert.That(CompositeKey.IsValid([]), Is.True);
    Assert.That(CompositeKey.IsValid(CompositeKey.Create(decimal.MinValue, 1e-28m, 1e28m, -1e-28m)), Is.True);
  }

  [Test]
  public void RandomKeysSortAsTheirElementsAndReadBack()
  {
    var random = new Random(20261115);
    var models = Enumerable.Range(0, 400).Select(_ => RandomTuple(random)).ToArray();
    var keys = models.Select(Encode).ToArray();
    for (var i = 0; i < models.Length; ++i)
    {
      Assert.That(CompositeKey.IsValid(keys[i]), Is.True);
      Assert.That(CompositeKey.ElementCount(keys[i]), Is.EqualTo(models[i].Length));
      for (var e = 0; e < models[i].Length; ++e)
        AssertReadsBack(keys[i], e, models[i][e]);
      for (var j = 0; j < models.Length; ++j)
      {
        Assert.That(Math.Sign(Order.Compare(keys[i], keys[j])), Is.EqualTo(Math.Sign(Compare(models[i], models[j]))),
            $"{Describe(models[i])} and {Describe(models[j])}");
      }
    }
  }

  /// <summary>An element of a key as a test models it.</summary>
  sealed record Element(CompositeElementType Type, object Value, bool Descending);

  static readonly object[] Strings = ["", "a", "a\0", "ab", "b", "é", "", "\U0001F600"];
  static readonly BigInteger[] Integers =
  [
    -BigInteger.Pow(2, 2000), -BigInteger.Pow(2, 100), Int128.MinValue, -BigInteger.Pow(2, 64), -(BigInteger)ulong.MaxValue,
    long.MinValue, -65536, -256, -255, -1, 0, 1, 255, 256, long.MaxValue, ulong.MaxValue, BigInteger.Pow(2, 64),
    UInt128.MaxValue, BigInteger.Pow(2, 2000)
  ];
  static readonly DateTimeOffset[] Instants =
  [
    DateTimeOffset.MinValue, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(3)),
    new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue
  ];
  static readonly DateOnly[] Dates = [DateOnly.MinValue, new DateOnly(2026, 11, 15), DateOnly.MaxValue];
  static readonly TimeOnly[] TimesOfDay = [TimeOnly.MinValue, new TimeOnly(12, 30), TimeOnly.MaxValue];
  static readonly TimeSpan[] Intervals = [TimeSpan.MinValue, TimeSpan.FromSeconds(-1), TimeSpan.Zero, TimeSpan.FromDays(1), TimeSpan.MaxValue];
  static readonly double[] Floats = [double.NegativeInfinity, -1.5, -double.Epsilon, 0, 1e-300, 1.5, double.PositiveInfinity, double.NaN];
  static readonly decimal[] Decimals = [decimal.MinValue, -10m, -1.5m, -0.1m, 0m, 0.1m, 0.11m, 1m, 10m, decimal.MaxValue];
  static readonly byte[][] ByteValues = [[], [0], [0, 1], [1], [255]];
  static readonly DateTime[] Times = [DateTime.MinValue, new DateTime(2026, 11, 15), DateTime.MaxValue];
  static readonly Guid[] Guids = [Guid.Empty, Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("ff000000-0000-0000-0000-000000000000")];

  static Element[] RandomTuple(Random random)
  {
    var length = random.Next(0, 5);
    var tuple = new Element[length];
    for (var i = 0; i < length; ++i)
    {
      var descending = random.Next(4) == 0;
      tuple[i] = random.Next(13) switch
      {
        0 => new Element(CompositeElementType.Null, null, descending),
        1 => new Element(CompositeElementType.Bytes, ByteValues[random.Next(ByteValues.Length)], descending),
        2 => new Element(CompositeElementType.String, Strings[random.Next(Strings.Length)], descending),
        3 => new Element(CompositeElementType.Integer, Integers[random.Next(Integers.Length)], descending),
        4 => new Element(CompositeElementType.Floating, Floats[random.Next(Floats.Length)], descending),
        5 => new Element(CompositeElementType.Decimal, Decimals[random.Next(Decimals.Length)], descending),
        6 => new Element(CompositeElementType.Boolean, random.Next(2) == 1, descending),
        7 => new Element(CompositeElementType.DateTime, Times[random.Next(Times.Length)], descending),
        8 => new Element(CompositeElementType.DateTimeOffset, Instants[random.Next(Instants.Length)], descending),
        9 => new Element(CompositeElementType.DateOnly, Dates[random.Next(Dates.Length)], descending),
        10 => new Element(CompositeElementType.TimeOnly, TimesOfDay[random.Next(TimesOfDay.Length)], descending),
        11 => new Element(CompositeElementType.TimeSpan, Intervals[random.Next(Intervals.Length)], descending),
        _ => new Element(CompositeElementType.Guid, Guids[random.Next(Guids.Length)], descending),
      };
    }
    return tuple;
  }

  /// <summary>A key built element by element, joined end to end.</summary>
  static byte[] Encode(Element[] tuple) => Join(tuple.Select(Encode).ToArray());

  static byte[] Encode(Element element)
  {
    byte[] Of<T>(T value) => element.Descending ? CompositeKey.Create(CompositeKey.Descending(value)) : CompositeKey.Create(value);
    return element.Type switch
    {
      CompositeElementType.Null => Of((string)null),
      CompositeElementType.Bytes => Of((byte[])element.Value),
      CompositeElementType.String => Of((string)element.Value),
      CompositeElementType.Integer => EncodeInteger((BigInteger)element.Value, Of, Of, Of, Of, Of),
      CompositeElementType.Floating => Of((double)element.Value),
      CompositeElementType.Decimal => Of((decimal)element.Value),
      CompositeElementType.Boolean => Of((bool)element.Value),
      CompositeElementType.DateTime => Of((DateTime)element.Value),
      CompositeElementType.DateTimeOffset => Of((DateTimeOffset)element.Value),
      CompositeElementType.DateOnly => Of((DateOnly)element.Value),
      CompositeElementType.TimeOnly => Of((TimeOnly)element.Value),
      CompositeElementType.TimeSpan => Of((TimeSpan)element.Value),
      _ => Of((Guid)element.Value)
    };
  }

  /// <summary>
  /// Writes an integer through the narrowest .NET type that holds it, or every third value through BigInteger,
  /// which writes the same bytes.
  /// </summary>
  static byte[] EncodeInteger(BigInteger value, Func<long, byte[]> int64, Func<ulong, byte[]> uint64,
      Func<Int128, byte[]> int128, Func<UInt128, byte[]> uint128, Func<BigInteger, byte[]> big)
  {
    if (value % 3 == 0)
      return big(value);
    if (value >= long.MinValue && value <= long.MaxValue)
      return int64((long)value);
    if (value >= 0 && value <= ulong.MaxValue)
      return uint64((ulong)value);
    if (value >= (BigInteger)Int128.MinValue && value <= (BigInteger)Int128.MaxValue)
      return int128((Int128)value);
    if (value >= 0 && value <= (BigInteger)UInt128.MaxValue)
      return uint128((UInt128)value);
    return big(value);
  }

  static void AssertReadsBack(byte[] key, int index, Element element)
  {
    Assert.That(CompositeKey.ElementType(key, index), Is.EqualTo(element.Type));
    Assert.That(CompositeKey.IsDescending(key, index), Is.EqualTo(element.Descending));
    object read = element.Type switch
    {
      CompositeElementType.Null => CompositeKey.Get<string>(key, index),
      CompositeElementType.Bytes => CompositeKey.Get<byte[]>(key, index),
      CompositeElementType.String => CompositeKey.Get<string>(key, index),
      CompositeElementType.Integer => CompositeKey.Get<BigInteger>(key, index),
      CompositeElementType.Floating => CompositeKey.Get<double>(key, index),
      CompositeElementType.Decimal => CompositeKey.Get<decimal>(key, index),
      CompositeElementType.Boolean => CompositeKey.Get<bool>(key, index),
      CompositeElementType.DateTime => CompositeKey.Get<DateTime>(key, index),
      CompositeElementType.DateTimeOffset => CompositeKey.Get<DateTimeOffset>(key, index),
      CompositeElementType.DateOnly => CompositeKey.Get<DateOnly>(key, index),
      CompositeElementType.TimeOnly => CompositeKey.Get<TimeOnly>(key, index),
      CompositeElementType.TimeSpan => CompositeKey.Get<TimeSpan>(key, index),
      _ => CompositeKey.Get<Guid>(key, index)
    };
    Assert.That(read, Is.EqualTo(element.Value));
    // An integer reads as every type that holds it.
    if (element.Value is BigInteger integer)
    {
      if (integer >= (BigInteger)Int128.MinValue && integer <= (BigInteger)Int128.MaxValue)
        Assert.That(CompositeKey.Get<Int128>(key, index), Is.EqualTo((Int128)integer));
      else
        Assert.Throws<OverflowException>(() => CompositeKey.Get<Int128>(key, index));
      if (integer >= long.MinValue && integer <= long.MaxValue)
        Assert.That(CompositeKey.Get<long>(key, index), Is.EqualTo((long)integer));
      else
        Assert.Throws<OverflowException>(() => CompositeKey.Get<long>(key, index));
    }
  }

  /// <summary>
  /// The order keys of the model must have: element by element, a key before every key that extends it;
  /// ascending elements before descending ones, by type and then by value, which descending elements reverse.
  /// </summary>
  static int Compare(Element[] x, Element[] y)
  {
    for (var i = 0; i < Math.Min(x.Length, y.Length); ++i)
    {
      var order = Compare(x[i], y[i]);
      if (order != 0)
        return order;
    }
    return x.Length.CompareTo(y.Length);
  }

  static int Compare(Element x, Element y)
  {
    if (x.Descending != y.Descending)
      return x.Descending ? 1 : -1;
    var order = x.Type != y.Type ? x.Type.CompareTo(y.Type) : CompareValues(x.Type, x.Value, y.Value);
    return x.Descending ? -order : order;
  }

  static int CompareValues(CompositeElementType type, object x, object y) => type switch
  {
    CompositeElementType.Null => 0,
    CompositeElementType.Bytes => ((byte[])x).AsSpan().SequenceCompareTo((byte[])y),
    CompositeElementType.String => CompareCodePoints((string)x, (string)y),
    CompositeElementType.Integer => ((BigInteger)x).CompareTo((BigInteger)y),
    CompositeElementType.Floating => CompareFloating((double)x, (double)y),
    CompositeElementType.Decimal => ((decimal)x).CompareTo((decimal)y),
    CompositeElementType.Boolean => ((bool)x).CompareTo((bool)y),
    CompositeElementType.DateTime => ((DateTime)x).Ticks.CompareTo(((DateTime)y).Ticks),
    CompositeElementType.DateTimeOffset => ((DateTimeOffset)x).UtcTicks.CompareTo(((DateTimeOffset)y).UtcTicks),
    CompositeElementType.DateOnly => ((DateOnly)x).CompareTo((DateOnly)y),
    CompositeElementType.TimeOnly => ((TimeOnly)x).CompareTo((TimeOnly)y),
    CompositeElementType.TimeSpan => ((TimeSpan)x).CompareTo((TimeSpan)y),
    _ => string.CompareOrdinal(((Guid)x).ToString("N"), ((Guid)y).ToString("N"))
  };

  static int CompareCodePoints(string x, string y)
  {
    var left = x.EnumerateRunes().Select(rune => rune.Value).ToArray();
    var right = y.EnumerateRunes().Select(rune => rune.Value).ToArray();
    return left.AsSpan().SequenceCompareTo(right);
  }

  /// <summary>Doubles by value with one NaN, which sorts after positive infinity.</summary>
  static int CompareFloating(double x, double y)
  {
    if (double.IsNaN(x) || double.IsNaN(y))
      return double.IsNaN(x).CompareTo(double.IsNaN(y));
    return x.CompareTo(y);
  }

  static string Describe(Element[] tuple)
      => "[" + string.Join(", ", tuple.Select(e => $"{(e.Descending ? "desc " : "")}{e.Type} {Show(e.Value)}")) + "]";

  static string Show(object value) => value switch
  {
    null => "null",
    byte[] bytes => Convert.ToHexString(bytes),
    string text => string.Concat(text.Select(c => c < 0x20 || c > 0x7E ? $"\\u{(int)c:X4}" : c.ToString())),
    _ => Convert.ToString(value, CultureInfo.InvariantCulture)
  };

  [Test]
  public void TheBuilderBuildsTheKeysCreateBuilds()
  {
    var placed = new DateTime(2026, 11, 15);
    var builder = new CompositeKeyBuilder();
    Assert.That(builder.Length, Is.Zero);
    Assert.That(builder.ToArray(), Is.Empty);
    var key = builder.Add("by-time").Add(CompositeKey.Descending(placed)).Add((string)null).Add<int?>(7)
        .Add(new byte[] { 0, 1 }).ToArray();
    Assert.That(key, Is.EqualTo(CompositeKey.Create("by-time", CompositeKey.Descending(placed), (string)null, (int?)7, new byte[] { 0, 1 })));
    Assert.That(builder.Length, Is.EqualTo(key.Length));
    Assert.That(builder.AsSpan().ToArray(), Is.EqualTo(key));
  }

  [Test]
  public void TheBuilderBuildsKeysOfAnyLength()
  {
    var builder = new CompositeKeyBuilder(capacity: 4);
    for (var i = 0; i < 20; ++i)
    {
      if (i % 2 == 0)
        builder.Add(i);
      else
        builder.Add(CompositeKey.Descending($"element {i}"));
    }
    var key = builder.ToArray();
    Assert.That(CompositeKey.ElementCount(key), Is.EqualTo(20));
    for (var i = 0; i < 20; ++i)
    {
      if (i % 2 == 0)
        Assert.That(CompositeKey.Get<int>(key, i), Is.EqualTo(i));
      else
        Assert.That(CompositeKey.Get<string>(key, i), Is.EqualTo($"element {i}"));
      Assert.That(CompositeKey.IsDescending(key, i), Is.EqualTo(i % 2 == 1));
    }
    Assert.That(CompositeKey.Decode<string, int>(CompositeKey.ElementBytes(key, 9, 2)), Is.EqualTo(("element 9", 10)));
    var reader = new CompositeKeyReader(key);
    var count = 0;
    while (reader.Next())
      ++count;
    Assert.That(count, Is.EqualTo(20));

    var text = new string('x', 10_000);
    Assert.That(CompositeKey.Get<string>(new CompositeKeyBuilder().Add(text).ToArray(), 0), Is.EqualTo(text));
  }

  [Test]
  public void ABuilderIsReusedAfterClear()
  {
    var builder = new CompositeKeyBuilder();
    var first = builder.Add("first").Add(1).ToArray();
    var second = builder.Clear().Add("second").ToArray();
    Assert.That(first, Is.EqualTo(CompositeKey.Create("first", 1)));
    Assert.That(second, Is.EqualTo(CompositeKey.Create("second")));
    Assert.That(builder.Length, Is.EqualTo(second.Length));
  }

  [Test]
  public void TheBuilderAppendsEncodedElements()
  {
    var row = CompositeKey.Create("row", "acme", 42L);
    var entry = new CompositeKeyBuilder().Add("by-customer").Add("bob").AddElements(CompositeKey.ElementBytes(row, 1, 2)).ToArray();
    Assert.That(entry, Is.EqualTo(CompositeKey.Create("by-customer", "bob", "acme", 42L)));
    Assert.That(new CompositeKeyBuilder().AddElements(row).AddElements([]).ToArray(), Is.EqualTo(row));
  }

  [Test]
  public void AFailedAppendLeavesTheKeyAsItWas()
  {
    var builder = new CompositeKeyBuilder().Add("kept");
    var before = builder.ToArray();
    Assert.Throws<NotSupportedException>(() => builder.Add(new Version(1, 2)));
    Assert.Throws<ArgumentException>(() => builder.Add("\uD800"));
    Assert.Throws<ArgumentException>(() => builder.AddElements(CompositeKey.Create("whole").AsSpan(0, 3)));
    Assert.Throws<ArgumentException>(() => builder.AddElements([0xFF]));
    Assert.That(builder.ToArray(), Is.EqualTo(before));
    Assert.Throws<ArgumentOutOfRangeException>(() => new CompositeKeyBuilder(-1));
  }

  [Test]
  public void TheComparersOrderKeysAndTextAsKeysSortThem()
  {
    // Code units below the surrogates, from U+E000 up, and surrogate pairs, alone, after a prefix, and with zeros.
    var emoji = char.ConvertFromUtf32(0x1F600);
    var halfWidth = char.ConvertFromUtf32(0xFF71);
    string[] texts =
    [
      "", "a", "a" + (char)0, "a" + (char)0 + "b", "a" + (char)1, "ab", "b", char.ConvertFromUtf32(0xD7FF),
      char.ConvertFromUtf32(0xE000), halfWidth, char.ConvertFromUtf32(0xFFFD), char.ConvertFromUtf32(0x10000), emoji,
      "x" + halfWidth, "x" + emoji, "x" + emoji + "y", emoji + halfWidth,
    ];
    // Each text comparer holds to the keys of the strings, nulls among them, in each of the four ways an element and a
    // tree can turn: the ascending one where both or neither are descending, the descending one where one of them is.
    var descendingOrder = new BytesLexicographicComparerDescending();
    string[] textsAndNull = [.. texts, null];
    foreach (var a in textsAndNull)
    {
      foreach (var b in textsAndNull)
      {
        var at = $"{(a == null ? "null" : Show(a))} and {(b == null ? "null" : Show(b))}";
        var ascending = Math.Sign(CompositeKey.TextComparerAscending.Compare(a, b));
        var descending = Math.Sign(CompositeKey.TextComparerDescending.Compare(a, b));
        var (up, down) = (CompositeKey.Create(a), CompositeKey.Create(b));
        var (upDescending, downDescending) = (CompositeKey.Create(CompositeKey.Descending(a)), CompositeKey.Create(CompositeKey.Descending(b)));
        Assert.That(ascending, Is.EqualTo(Math.Sign(Order.Compare(up, down))), at);
        Assert.That(descending, Is.EqualTo(Math.Sign(Order.Compare(upDescending, downDescending))), at);
        Assert.That(descending, Is.EqualTo(Math.Sign(descendingOrder.Compare(up, down))), at);
        Assert.That(ascending, Is.EqualTo(Math.Sign(descendingOrder.Compare(upDescending, downDescending))), at);
        Assert.That(CompositeKey.TextComparerAscending.Equals(a, b), Is.EqualTo(a == b), at);
        Assert.That(CompositeKey.TextComparerDescending.Equals(a, b), Is.EqualTo(a == b), at);
      }
    }
    // Ordinal order differs from the keys' where a surrogate pair meets a character from U+E000 up.
    Assert.That(string.CompareOrdinal(emoji, halfWidth), Is.LessThan(0));
    Assert.That(CompositeKey.TextComparerAscending.Compare(emoji, halfWidth), Is.GreaterThan(0));
    Assert.That(CompositeKey.TextComparerDescending.Compare(emoji, halfWidth), Is.LessThan(0));
    Assert.That(texts.Order(CompositeKey.TextComparerAscending).Select(text => CompositeKey.Create(text)),
        Is.EqualTo(texts.Select(text => CompositeKey.Create(text)).Order(CompositeKey.ComparerAscending)));
    Assert.That(texts.Order(CompositeKey.TextComparerDescending).Select(text => CompositeKey.Create(text)),
        Is.EqualTo(texts.Select(text => CompositeKey.Create(text)).Order(CompositeKey.ComparerDescending)));
    // A null comes first in the ascending order and last in the descending one, as a null element does in keys.
    Assert.That(CompositeKey.TextComparerAscending.Compare(null, ""), Is.LessThan(0));
    Assert.That(CompositeKey.TextComparerDescending.Compare(null, ""), Is.GreaterThan(0));
    Assert.Throws<ArgumentNullException>(() => CompositeKey.TextComparerDescending.GetHashCode(null));

    // The comparers are written out rather than taken from the trees' comparers, so they are held to them here: on
    // every pair of the keys of the texts and of short bytes, nulls among them, which the trees take as empty.
    var bytes = texts.Select(text => CompositeKey.Create(text)).Concat(AllBytes(2, [0x00, 0x01, 0x7F, 0xFF])).Append(null).ToList();
    foreach (var a in bytes)
    {
      foreach (var b in bytes)
      {
        var at = $"0x{Convert.ToHexString(a ?? [])}{(a == null ? " (null)" : "")} and 0x{Convert.ToHexString(b ?? [])}{(b == null ? " (null)" : "")}";
        Assert.That(Math.Sign(CompositeKey.ComparerAscending.Compare(a, b)), Is.EqualTo(Math.Sign(Order.Compare(a, b))), at);
        Assert.That(Math.Sign(CompositeKey.ComparerDescending.Compare(a, b)), Is.EqualTo(Math.Sign(descendingOrder.Compare(a, b))), at);
        // Both take as one key what the trees do, and hash those keys alike.
        var same = Order.Compare(a, b) == 0;
        Assert.That(CompositeKey.ComparerAscending.Equals(a, b), Is.EqualTo(same), at);
        Assert.That(CompositeKey.ComparerDescending.Equals(a, b), Is.EqualTo(same), at);
        if (same)
        {
          Assert.That(CompositeKey.ComparerAscending.GetHashCode(a), Is.EqualTo(CompositeKey.ComparerAscending.GetHashCode(b)), at);
          Assert.That(CompositeKey.ComparerDescending.GetHashCode(a), Is.EqualTo(CompositeKey.ComparerAscending.GetHashCode(b)), at);
        }
      }
    }

    // Keys with the same bytes are one key in a set, whatever arrays hold them.
    var key = CompositeKey.Create("bob", 42L);
    var copy = key.ToArray();
    Assert.That(key == copy, Is.False);
    Assert.That(new HashSet<byte[]>([key, copy], CompositeKey.ComparerAscending), Has.Count.EqualTo(1));
    Assert.That(new HashSet<byte[]>([key, copy, [], []], CompositeKey.ComparerDescending), Has.Count.EqualTo(2));
  }

  [Test]
  public void InvalidTextIsRefusedWithWhereItFails()
  {
    var lone = "ab" + (char)0xD800 + "c";
    var write = Assert.Throws<ArgumentException>(() => CompositeKey.Create("ok", lone));
    Assert.That(write.Message, Does.Contain("A string element is not valid UTF-16").And.Contain("index 2"));
    Assert.That(Assert.Throws<ArgumentException>(() => CompositeValue.Create(lone)).Message, Does.Contain("index 2"));
    Assert.That(Assert.Throws<ArgumentException>(() => new CompositeValueBuilder().Add(lone)).Message, Does.Contain("index 2"));
    var text = Assert.Throws<ArgumentException>(() => CompositeKey.BeginsWith([], lone));
    Assert.That((text.ParamName, text.Message.StartsWith("The text is not valid UTF-16", StringComparison.Ordinal)), Is.EqualTo(("text", true)));

    // A read names the element whose bytes are not UTF-8.
    byte[] key = [.. CompositeKey.Create("ok"), 0x03, 0xC3, 0x28, 0x00, 0x01];
    Assert.That(Assert.Throws<ArgumentException>(() => CompositeKey.Get<string>(key, 1)).Message,
        Does.StartWith("Element 1 is a string whose bytes are not valid UTF-8"));
    var value = CompositeValue.Create("ok", "ab");
    value[^2] = 0xC3;
    value[^1] = 0x28;
    Assert.That(Assert.Throws<ArgumentException>(() => CompositeValue.Get<string>(value, 1)).Message,
        Does.StartWith("Element 1 is a string whose bytes are not valid UTF-8"));
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void RowsAndTheirIndexesShareATree(DiskSegmentMode mode)
  {
    var provider = new InMemoryFileStreamProvider();
    // An empty byte array is a deletion, so an index entry holds one byte.
    byte[] entry = [1];
    (long Id, string Customer, DateTime Placed)[] orders =
    [
      (1, "bob", new DateTime(2026, 11, 1)),
      (2, "alice", new DateTime(2026, 11, 3)),
      (3, "bob", new DateTime(2026, 11, 2)),
      (4, "carol", new DateTime(2026, 11, 4)),
      (5, "bob", new DateTime(2026, 11, 5)),
    ];

    using (var tree = CreateFactory(provider, mode).Create())
    {
      foreach (var (id, customer, placed) in orders)
      {
        tree.Upsert(CompositeKey.Create("row", id), Encoding.UTF8.GetBytes(customer));
        tree.Upsert(CompositeKey.Create("by-customer", customer, id), entry);
        tree.Upsert(CompositeKey.Create("by-time", CompositeKey.Descending(placed), id), entry);
      }
      AssertIndexes(tree);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
      AssertIndexes(tree);
    }

    using (var tree = CreateFactory(provider, mode).Open())
      AssertIndexes(tree);
  }

  static void AssertIndexes(IZoneTree<byte[], byte[]> tree)
  {
    var bobs = Scan(tree, CompositeKey.Create("by-customer", "bob")).Select(key => CompositeKey.Get<long>(key, 2));
    Assert.That(bobs, Is.EqualTo(new[] { 1L, 3L, 5L }));
    var newestFirst = Scan(tree, CompositeKey.Create("by-time")).Select(key => CompositeKey.Get<long>(key, 2));
    Assert.That(newestFirst, Is.EqualTo(new[] { 5L, 4L, 2L, 3L, 1L }));
    var rows = Scan(tree, CompositeKey.Create("row")).Select(key => CompositeKey.Decode<string, long>(key).Item2);
    Assert.That(rows, Is.EqualTo(new[] { 1L, 2L, 3L, 4L, 5L }));

    // An index entry leads back to its row.
    var first = Scan(tree, CompositeKey.Create("by-time")).First();
    byte[] row = [.. CompositeKey.Create("row"), .. CompositeKey.ElementBytes(first, 2, 1)];
    Assert.That(tree.TryGet(row, out var customer), Is.True);
    Assert.That(Encoding.UTF8.GetString(customer), Is.EqualTo("bob"));
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void TheEmptyKeyIsStoredAndSortsBeforeEveryKey(DiskSegmentMode mode)
  {
    var provider = new InMemoryFileStreamProvider();
    // In key order: the empty key, then a string, a key under it, and an integer, whose tag sorts after strings'.
    byte[][] keys = [[], CompositeKey.Create("a"), CompositeKey.Create("a", 2), CompositeKey.Create(1)];
    using (var tree = CreateFactory(provider, mode).Create())
    {
      for (var i = 0; i < keys.Length; ++i)
        tree.Upsert(keys[i], [(byte)i]);
      AssertEmptyKey(tree, keys);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
      AssertEmptyKey(tree, keys);
    }
    using (var tree = CreateFactory(provider, mode).Open())
      AssertEmptyKey(tree, keys);
  }

  static void AssertEmptyKey(IZoneTree<byte[], byte[]> tree, byte[][] keys)
  {
    Assert.That(tree.TryGet([], out var value), Is.True);
    Assert.That(value, Is.EqualTo(new byte[] { 0 }));
    (string Name, CompositeKeyRange Range, byte[][] Keys)[] cases =
    [
      ("Range([])", CompositeKey.Range([]), keys),
      ("AtLeast([])", CompositeKey.AtLeast([]), keys),
      ("GreaterThan([])", CompositeKey.GreaterThan([]), []),
      ("AtMost([])", CompositeKey.AtMost([]), keys),
      ("LessThan([])", CompositeKey.LessThan([]), []),
    ];
    foreach (var (name, range, held) in cases)
    {
      Assert.That(Read(tree.CreateIterator(), range.Lower, range), Is.EqualTo(held), $"{name} forward");
      Assert.That(Read(tree.CreateReverseIterator(), range.Upper, range), Is.EqualTo(Enumerable.Reverse(held)),
          $"{name} backward");
    }
  }

  /// <summary>The keys of a range from where an iterator seeks, while the range contains them.</summary>
  static List<byte[]> Read(IZoneTreeIterator<byte[], byte[]> iterator, byte[] start, CompositeKeyRange range)
  {
    using (iterator)
    {
      var keys = new List<byte[]>();
      iterator.Seek(start);
      while (iterator.Next() && range.Contains(iterator.CurrentKey))
        keys.Add(iterator.CurrentKey);
      return keys;
    }
  }

  static List<byte[]> Scan(IZoneTree<byte[], byte[]> tree, byte[] prefix)
  {
    var range = CompositeKey.Range(prefix);
    var keys = new List<byte[]>();
    using var iterator = tree.CreateIterator();
    iterator.Seek(range.Lower);
    while (iterator.Next() && range.Contains(iterator.CurrentKey))
      keys.Add(iterator.CurrentKey);
    return keys;
  }

  static ZoneTreeFactory<byte[], byte[]> CreateFactory(InMemoryFileStreamProvider provider, DiskSegmentMode mode)
  {
    return new ZoneTreeFactory<byte[], byte[]>(provider)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.Private)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DiskSegmentMode = mode;
          options.MinimumRecordCount = 2;
          options.MaximumRecordCount = 3;
        });
  }
}
