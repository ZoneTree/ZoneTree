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
      var (from, to) = CompositeKey.Range(EncodeAscending(prefixTuple));
      for (var i = 0; i < tuples.Length; ++i)
      {
        var inRange = Order.Compare(keys[i], from) >= 0 && Order.Compare(keys[i], to) <= 0;
        var isUnder = tuples[i].Length >= prefixTuple.Length && tuples[i].Take(prefixTuple.Length).SequenceEqual(prefixTuple);
        Assert.That(inRange, Is.EqualTo(isUnder), $"[{string.Join(", ", tuples[i])}] for [{string.Join(", ", prefixTuple)}]");
      }
    }

    // A descending prefix holds its extensions and nothing that only starts with its bytes.
    var (low, high) = CompositeKey.Range(CompositeKey.Create(CompositeKey.Descending("a")));
    bool In(byte[] key) => Order.Compare(key, low) >= 0 && Order.Compare(key, high) <= 0;
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
      var (from, to) = CompositeKey.BeginsWith(CompositeKey.Create("by-name"), text, order);
      for (var i = 0; i < keys.Count; ++i)
      {
        var inRange = Order.Compare(keys[i], from) >= 0 && Order.Compare(keys[i], to) <= 0;
        var beginsWith = i < names.Length && names[i].StartsWith(text, StringComparison.Ordinal);
        Assert.That(inRange, Is.EqualTo(beginsWith), $"{(i < names.Length ? Show(names[i]) : $"key {i}")} for {Show(text)}");
      }
    }

    // A string the keys start with, with no prefix.
    var (bare, bareEnd) = CompositeKey.BeginsWith([], "bo", order);
    var bob = order == CompositeKeyOrder.Ascending ? CompositeKey.Create("bob") : CompositeKey.Create(CompositeKey.Descending("bob"));
    Assert.That(Order.Compare(bob, bare) >= 0 && Order.Compare(bob, bareEnd) <= 0, Is.True);
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
      var (from, to) = CompositeKey.BeginsWith(CompositeKey.Create("hash"), start, order);
      foreach (var value in values)
      {
        var key = Key(value);
        var inRange = Order.Compare(key, from) >= 0 && Order.Compare(key, to) <= 0;
        Assert.That(inRange, Is.EqualTo(value.AsSpan().StartsWith(start)), $"{Convert.ToHexString(value)} for {Convert.ToHexString(start)}");
      }
    }
    Assert.Throws<ArgumentException>(() => CompositeKey.BeginsWith([0x03, 0x61], [1], order));
  }

  [Test]
  public void BeginsWithChecksItsArguments()
  {
    Assert.Throws<ArgumentException>(() => CompositeKey.BeginsWith([0x03, 0x61], "bo"));
    Assert.Throws<EncoderFallbackException>(() => CompositeKey.BeginsWith([], "\uD800"));
    Assert.Throws<ArgumentNullException>(() => CompositeKey.BeginsWith([], (string)null));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompositeKey.BeginsWith([], "bo", (CompositeKeyOrder)5));
    var longText = new string('x', 400);
    var (from, to) = CompositeKey.BeginsWith([], longText);
    var key = CompositeKey.Create(longText + "y");
    Assert.That(Order.Compare(key, from) >= 0 && Order.Compare(key, to) <= 0, Is.True);
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
    Assert.Throws<EncoderFallbackException>(() => CompositeKey.Create("\uD800"));
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
    Assert.Throws<DecoderFallbackException>(() => CompositeKey.Get<string>(invalidUtf8, 0));

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
    Assert.Throws<EncoderFallbackException>(() => builder.Add("\uD800"));
    Assert.Throws<ArgumentException>(() => builder.AddElements(CompositeKey.Create("whole").AsSpan(0, 3)));
    Assert.Throws<ArgumentException>(() => builder.AddElements([0xFF]));
    Assert.That(builder.ToArray(), Is.EqualTo(before));
    Assert.Throws<ArgumentOutOfRangeException>(() => new CompositeKeyBuilder(-1));
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

  static List<byte[]> Scan(IZoneTree<byte[], byte[]> tree, byte[] prefix)
  {
    var (from, to) = CompositeKey.Range(prefix);
    var keys = new List<byte[]>();
    using var iterator = tree.CreateIterator();
    iterator.Seek(from);
    while (iterator.Next() && Order.Compare(iterator.CurrentKey, to) <= 0)
      keys.Add(iterator.CurrentKey);
    return keys;
  }

  static ZoneTreeFactory<byte[], byte[]> CreateFactory(InMemoryFileStreamProvider provider, DiskSegmentMode mode)
  {
    return new ZoneTreeFactory<byte[], byte[]>(provider)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.Sync)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DiskSegmentMode = mode;
          options.MinimumRecordCount = 2;
          options.MaximumRecordCount = 3;
        });
  }
}
