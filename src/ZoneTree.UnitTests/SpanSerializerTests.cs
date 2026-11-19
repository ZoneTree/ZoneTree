using System.Numerics;
using ZoneTree.PresetTypes;
using ZoneTree.Serializers;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.UnitTests;

/// <summary>
/// Tests that every serializer that writes into a span writes exactly the bytes its array form returns, since both
/// forms write the same files. Each write goes into a buffer full of 0xFF, so a byte the span form leaves unwritten
/// shows up as a difference.
/// </summary>
public sealed class SpanSerializerTests
{
  [Test]
  public void EverySpanSerializerWritesTheBytesOfItsArrayForm()
  {
    Assert.Multiple(() =>
    {
      Check(new ByteSerializer(), (byte)0, (byte)1, byte.MaxValue);
      Check(new SByteSerializer(), (sbyte)0, sbyte.MinValue, sbyte.MaxValue);
      Check(new BooleanSerializer(), false, true);
      Check(new CharSerializer(), 'a', '\0', char.MaxValue);
      Check(new Int16Serializer(), (short)0, short.MinValue, short.MaxValue);
      Check(new UInt16Serializer(), (ushort)0, ushort.MaxValue);
      Check(new Int32Serializer(), 0, -1, int.MinValue, int.MaxValue);
      Check(new UInt32Serializer(), 0u, uint.MaxValue);
      Check(new Int64Serializer(), 0L, -1L, long.MinValue, long.MaxValue);
      Check(new UInt64Serializer(), 0UL, ulong.MaxValue);
      Check(new Int128Serializer(), Int128.Zero, Int128.MinValue, Int128.MaxValue, (Int128)(-12345));
      Check(new UInt128Serializer(), UInt128.Zero, UInt128.MaxValue, (UInt128)12345);
      Check(new IntPtrSerializer(), (nint)0, (nint)(-1), nint.MaxValue);
      Check(new UIntPtrSerializer(), (nuint)0, nuint.MaxValue);
      Check(new SingleSerializer(), 0f, -1.5f, float.NaN, float.MaxValue);
      Check(new DoubleSerializer(), 0d, -1.5d, double.NegativeInfinity, double.MaxValue);
      Check(new HalfSerializer(), (Half)0, (Half)(-1.5f), Half.MaxValue);
      Check(new DecimalSerializer(), 0m, -1.5m, decimal.MinValue, decimal.MaxValue, 79228162514264.337593543950335m);
      Check(new GuidSerializer(), Guid.Empty, Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));
      Check(new DateTimeSerializer(), DateTime.MinValue, DateTime.MaxValue, new DateTime(2026, 11, 19, 1, 2, 3, DateTimeKind.Utc));
      Check(new DateTimeOffsetSerializer(), DateTimeOffset.MinValue, new DateTimeOffset(2026, 11, 19, 1, 2, 3, TimeSpan.FromHours(-5)));
      Check(new DateOnlySerializer(), DateOnly.MinValue, DateOnly.MaxValue, new DateOnly(2026, 11, 19));
      Check(new TimeOnlySerializer(), TimeOnly.MinValue, TimeOnly.MaxValue, new TimeOnly(13, 14, 15));
      Check(new TimeSpanSerializer(), TimeSpan.Zero, TimeSpan.MinValue, TimeSpan.MaxValue);
      Check(new BigIntegerSerializer(), BigInteger.Zero, BigInteger.MinusOne, BigInteger.Pow(2, 200), -BigInteger.Pow(3, 101));
      Check(new EnumSerializer<DayOfWeek>(), DayOfWeek.Sunday, DayOfWeek.Saturday);
      Check(new StructSerializer<(int, long)>(), (1, 2L), (-1, long.MaxValue));
      Check(new DeletableSerializer<long>(), new Deletable<long>(5, false), new Deletable<long>(-5, true));
      Check(new Utf8StringSerializer(), null, "", "abc", "ğüşıöç", "😀 emoji", new string('x', 1000),
          new string('ğ', 16 * 1024), new string('ğ', 16 * 1024 + 1) + "😀");
      Check(new UnicodeStringSerializer(), null, "", "abc", "ğüşıöç", "😀 emoji", new string('x', 1000));
      Check(new DeletableRefSerializer<string>(new Utf8StringSerializer()),
          new Deletable<string>(null, false), new Deletable<string>("", true), new Deletable<string>("ğüş", false));
      // A null array reads back as an empty one.
      CheckAs(new BytesSerializer(), x => x ?? Array.Empty<byte>(), null, [], [1, 2, 3]);
      CheckAs(new ByteArraySerializer(), x => x.ToArray(), Memory<byte>.Empty, new byte[] { 1, 2, 3 });
      CheckAs(new ReadOnlyByteMemorySerializer(), x => x.ToArray(), ReadOnlyMemory<byte>.Empty, new byte[] { 1, 2, 3 });
      // Fixed size versioned keys and values are padded to the size of the versioned type.
      Check(new VersionedKeySerializer<long>(new Int64Serializer()),
          new VersionedKey<long>(VersionedKeyKind.Data, -7, long.MaxValue),
          new VersionedKey<long>(VersionedKeyKind.Clock, default, 3));
      Check(new VersionedKeySerializer<string>(new Utf8StringSerializer()),
          new VersionedKey<string>(VersionedKeyKind.Data, "ğüş", 5),
          new VersionedKey<string>(VersionedKeyKind.Data, null, 6),
          new VersionedKey<string>(VersionedKeyKind.TransactionStatus, default, 7));
      Check(new VersionedValueSerializer<int>(new Int32Serializer()),
          VersionedValue<int>.FromValue(-9), VersionedValue<int>.FromSystem(long.MinValue),
          new VersionedValue<int>(VersionedValueKind.Tombstone, 0, default));
      Check(new VersionedValueSerializer<string>(new Utf8StringSerializer()),
          VersionedValue<string>.FromValue("ğüş"), VersionedValue<string>.FromValue(null),
          VersionedValue<string>.FromSystem(12), new VersionedValue<string>(VersionedValueKind.Tombstone, 0, default));
    });
  }

  [Test]
  public void Utf8StringsLongerThan16KCharsNeedTheirByteCountInsteadOfTheBound()
  {
    var serializer = new Utf8StringSerializer();
    var bounded = new string('x', 16 * 1024);
    var counted = new string('ğ', 16 * 1024 + 1) + "😀";
    Assert.Multiple(() =>
    {
      Assert.That(serializer.GetMaxByteCount(bounded),
          Is.EqualTo(System.Text.Encoding.UTF8.GetMaxByteCount(bounded.Length)));
      Assert.That(serializer.GetMaxByteCount(counted), Is.EqualTo(System.Text.Encoding.UTF8.GetByteCount(counted)));
    });
  }

  static void Check<T>(ISerializer<T> serializer, params T[] values) => CheckAs(serializer, x => x, values);

  // Compares round trips through the view, for entries that compare by reference.
  static void CheckAs<T>(ISerializer<T> serializer, Func<T, object> view, params T[] values)
  {
    var name = serializer.GetType().Name;
    foreach (var value in values)
    {
      var expected = serializer.Serialize(value).ToArray();
      var maxByteCount = serializer.GetMaxByteCount(value);
      var buffer = Enumerable.Repeat((byte)0xFF, maxByteCount + 8).ToArray();
      var written = serializer.Serialize(value, buffer);
      Assert.That(written, Is.LessThanOrEqualTo(maxByteCount), $"{name} {value}: more bytes than the maximum");
      Assert.That(buffer.AsSpan(0, written).ToArray(), Is.EqualTo(expected), $"{name} {value}: different bytes");
      Assert.That(view(serializer.Deserialize(buffer.AsMemory(0, written))), Is.EqualTo(view(value)),
          $"{name} {value}: round trip");
    }
  }
}
