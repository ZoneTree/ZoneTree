using System.Numerics;
using System.Runtime.CompilerServices;
using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Options;
using ZoneTree.PresetTypes;
using ZoneTree.Serializers;

namespace ZoneTree.UnitTests;

/// <summary>The built-in components of the types a tree supports by default, and trees keyed and valued by them.</summary>
public sealed class BuiltInTypesTests
{
  enum Status { Draft = -1, Open = 0, Closed = 7 }

  enum Wide : ulong { Low = 1, High = ulong.MaxValue }

  [Test]
  public void EveryBuiltInTypeHasComponentsThatAgree()
  {
    AssertComponents<sbyte>(sbyte.MinValue, -1, 0, 1, sbyte.MaxValue);
    AssertComponents<float>(float.NaN, float.NegativeInfinity, -1.5f, 0f, float.Epsilon, 1.5f, float.PositiveInfinity);
    AssertComponents<Half>(Half.NaN, Half.NegativeInfinity, (Half)(-1.5), Half.Zero, Half.Epsilon, (Half)1.5, Half.PositiveInfinity);
    AssertComponents<nint>(nint.MinValue, -1, 0, 1, nint.MaxValue);
    AssertComponents<nuint>(0, 1, nuint.MaxValue);
    AssertComponents<Int128>(Int128.MinValue, long.MinValue, -1, 0, 1, ulong.MaxValue, Int128.MaxValue);
    AssertComponents<UInt128>(0, 1, ulong.MaxValue, UInt128.MaxValue);
    AssertComponents<BigInteger>(-BigInteger.Pow(2, 300), long.MinValue, -1, 0, 1, ulong.MaxValue, BigInteger.Pow(2, 300));
    AssertComponents<TimeSpan>(TimeSpan.MinValue, TimeSpan.FromTicks(-1), TimeSpan.Zero, TimeSpan.FromDays(1), TimeSpan.MaxValue);
    AssertComponents<DateTimeOffset>(DateTimeOffset.MinValue, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(5)),
        new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue);
    AssertComponents<DateOnly>(DateOnly.MinValue, new DateOnly(2026, 11, 15), DateOnly.MaxValue);
    AssertComponents<TimeOnly>(TimeOnly.MinValue, new TimeOnly(12, 30), TimeOnly.MaxValue);
    AssertComponents<bool>(false, true);
    AssertComponents<Status>(Status.Draft, Status.Open, Status.Closed);
    AssertComponents<Wide>(Wide.Low, Wide.High);
    AssertComponents<ReadOnlyMemory<byte>>(Array.Empty<byte>(), new byte[] { 0 }, new byte[] { 0, 1 }, new byte[] { 1 });
    AssertComponents<byte>(0, 1, byte.MaxValue);
    AssertComponents<char>('\0', 'A', 'ÿ', 'Ā', 'Ж', '￿');
    AssertComponents<short>(short.MinValue, -1, 0, 1, short.MaxValue);
    AssertComponents<ushort>(0, 1, ushort.MaxValue);
    AssertComponents<int>(int.MinValue, -1, 0, 1, int.MaxValue);
    AssertComponents<uint>(0, 1, uint.MaxValue);
    AssertComponents<long>(long.MinValue, -1, 0, 1, long.MaxValue);
    AssertComponents<ulong>(0, 1, ulong.MaxValue);
    AssertComponents<double>(double.NaN, double.NegativeInfinity, -1.5, 0, double.Epsilon, 1.5, double.PositiveInfinity);
    AssertComponents<decimal>(decimal.MinValue, -1.5m, -0.001m, 0m, 0.001m, 1.50m, decimal.MaxValue);
    AssertComponents<DateTime>(DateTime.MinValue, new DateTime(2026, 11, 15), DateTime.MaxValue);
    AssertComponents<Guid>(Guid.Empty, Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("01000000-0000-0000-0000-000000000000"));
  }

  [Test]
  public void BuiltInSerializersWriteLittleEndian()
  {
    AssertBytes<short>(0x0102, [0x02, 0x01]);
    AssertBytes<ushort>(0x0102, [0x02, 0x01]);
    AssertBytes(0x01020304, [0x04, 0x03, 0x02, 0x01]);
    AssertBytes(0x01020304u, [0x04, 0x03, 0x02, 0x01]);
    AssertBytes(0x0102030405060708L, [0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01]);
    AssertBytes(0x0102030405060708UL, [0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01]);
    AssertBytes<sbyte>(-2, [0xFE]);
    AssertBytes<byte>(0xAB, [0xAB]);
    AssertBytes(true, [0x01]);
    AssertBytes('Ж', [0x16, 0x04]);
    AssertBytes(1.0, [0, 0, 0, 0, 0, 0, 0xF0, 0x3F]);
    AssertBytes(1.0f, [0, 0, 0x80, 0x3F]);
    AssertBytes((Half)1, [0x00, 0x3C]);
    AssertBytes(new DateTime(0x0102030405060708), [0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01]);
    AssertBytes(new TimeSpan(-2), [0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
    AssertBytes(DateOnly.FromDayNumber(0x010203), [0x03, 0x02, 0x01, 0x00]);
    AssertBytes(new TimeOnly(0x0102030405), [0x05, 0x04, 0x03, 0x02, 0x01, 0, 0, 0]);
    AssertBytes(new Int128(1, 2), [2, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0]);
    AssertBytes(new UInt128(1, 2), [2, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0]);
    AssertBytes(Status.Closed, [0x07, 0x00, 0x00, 0x00]);
    AssertBytes(Wide.High, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
    // A decimal is its sign and scale, the high 32 bits of its coefficient, then the low 64 bits.
    AssertBytes(-1.5m, [0x00, 0x00, 0x01, 0x80, 0, 0, 0, 0, 0x0F, 0, 0, 0, 0, 0, 0, 0]);
    var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    AssertBytes(guid, guid.ToByteArray());
    if (IntPtr.Size == sizeof(long))
    {
      AssertBytes<nint>(-2, [0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
      AssertBytes<nuint>(2, [0x02, 0, 0, 0, 0, 0, 0, 0]);
    }
  }

  /// <summary>Checks that a type's built-in serializer writes a value as these bytes and reads them back as it.</summary>
  static void AssertBytes<T>(T value, byte[] expected)
  {
    var serializer = ComponentsForKnownTypes.GetSerializer<T>();
    Assert.That(serializer.Serialize(value).ToArray(), Is.EqualTo(expected), typeof(T).Name);
    Assert.That(serializer.Deserialize(expected.ToArray()), Is.EqualTo(value), typeof(T).Name);
  }

  /// <summary>
  /// Checks a type's built-in comparers, hasher and serializer: the comparers order the values given in order,
  /// the hasher agrees with the comparer, and the serializer writes a fixed-size type in exactly its size.
  /// </summary>
  static void AssertComponents<T>(params T[] ordered)
  {
    var comparer = ComponentsForKnownTypes.GetComparer<T>();
    var hasher = ComponentsForKnownTypes.GetKeyHasher<T>();
    var serializer = ComponentsForKnownTypes.GetSerializer<T>();
    Assert.That(comparer, Is.Not.Null, typeof(T).Name);
    Assert.That(hasher, Is.Not.Null, typeof(T).Name);
    Assert.That(serializer, Is.Not.Null, typeof(T).Name);
    var descending = Descending(comparer);
    for (var i = 0; i < ordered.Length; ++i)
    {
      for (var j = 0; j < ordered.Length; ++j)
      {
        Assert.That(Math.Sign(comparer.Compare(ordered[i], ordered[j])), Is.EqualTo(Math.Sign(i.CompareTo(j))), $"{ordered[i]} and {ordered[j]}");
        if (descending != null)
          Assert.That(Math.Sign(descending.Compare(ordered[i], ordered[j])), Is.EqualTo(Math.Sign(j.CompareTo(i))), $"{ordered[i]} and {ordered[j]}");
      }
      var bytes = serializer.Serialize(ordered[i]);
      if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        Assert.That(bytes.Length, Is.EqualTo(Unsafe.SizeOf<T>()), typeof(T).Name);
      Assert.That(comparer.Compare(serializer.Deserialize(bytes), ordered[i]), Is.Zero, $"{ordered[i]}");
      Assert.That(hasher.GetHashCode(serializer.Deserialize(bytes)), Is.EqualTo(hasher.GetHashCode(ordered[i])), $"{ordered[i]}");
    }
  }

  /// <summary>The descending sibling of a built-in ascending comparer, or null for a type with none.</summary>
  static IRefComparer<T> Descending<T>(IRefComparer<T> ascending)
      => ascending switch
      {
        ByteComparerAscending or CharComparerAscending or GuidComparerAscending => null,
        Int16ComparerAscending => (IRefComparer<T>)(object)new Int16ComparerDescending(),
        UInt16ComparerAscending => (IRefComparer<T>)(object)new UInt16ComparerDescending(),
        Int32ComparerAscending => (IRefComparer<T>)(object)new Int32ComparerDescending(),
        UInt32ComparerAscending => (IRefComparer<T>)(object)new UInt32ComparerDescending(),
        Int64ComparerAscending => (IRefComparer<T>)(object)new Int64ComparerDescending(),
        UInt64ComparerAscending => (IRefComparer<T>)(object)new UInt64ComparerDescending(),
        DoubleComparerAscending => (IRefComparer<T>)(object)new DoubleComparerDescending(),
        DecimalComparerAscending => (IRefComparer<T>)(object)new DecimalComparerDescending(),
        DateTimeComparerAscending => (IRefComparer<T>)(object)new DateTimeComparerDescending(),
        SByteComparerAscending => (IRefComparer<T>)(object)new SByteComparerDescending(),
        SingleComparerAscending => (IRefComparer<T>)(object)new SingleComparerDescending(),
        HalfComparerAscending => (IRefComparer<T>)(object)new HalfComparerDescending(),
        IntPtrComparerAscending => (IRefComparer<T>)(object)new IntPtrComparerDescending(),
        UIntPtrComparerAscending => (IRefComparer<T>)(object)new UIntPtrComparerDescending(),
        Int128ComparerAscending => (IRefComparer<T>)(object)new Int128ComparerDescending(),
        UInt128ComparerAscending => (IRefComparer<T>)(object)new UInt128ComparerDescending(),
        BigIntegerComparerAscending => (IRefComparer<T>)(object)new BigIntegerComparerDescending(),
        TimeSpanComparerAscending => (IRefComparer<T>)(object)new TimeSpanComparerDescending(),
        DateTimeOffsetComparerAscending => (IRefComparer<T>)(object)new DateTimeOffsetComparerDescending(),
        DateOnlyComparerAscending => (IRefComparer<T>)(object)new DateOnlyComparerDescending(),
        TimeOnlyComparerAscending => (IRefComparer<T>)(object)new TimeOnlyComparerDescending(),
        BooleanComparerAscending => (IRefComparer<T>)(object)new BooleanComparerDescending(),
        ReadOnlyByteMemoryLexicographicComparerAscending => (IRefComparer<T>)(object)new ReadOnlyByteMemoryLexicographicComparerDescending(),
        EnumComparerAscending<T> => new EnumComparerDescending<T>(),
        _ => throw new ArgumentException(ascending.GetType().Name)
      };

  [Test]
  public void EqualValuesHashAlike()
  {
    var instant = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(5));
    AssertEqual(instant, instant.ToUniversalTime());
    AssertEqual(0f, -0f);
    AssertEqual(float.NaN, BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00001)));
    AssertEqual(Half.Zero, Half.NegativeZero);
    AssertEqual<ReadOnlyMemory<byte>>(new byte[] { 1, 2 }, new byte[] { 9, 1, 2 }.AsMemory(1));
  }

  static void AssertEqual<T>(T x, T y)
  {
    Assert.That(ComponentsForKnownTypes.GetComparer<T>().Compare(x, y), Is.Zero, $"{x} and {y}");
    var hasher = ComponentsForKnownTypes.GetKeyHasher<T>();
    Assert.That(hasher.GetHashCode(x), Is.EqualTo(hasher.GetHashCode(y)), $"{x} and {y}");
  }

  [Test]
  public void TheOffsetOfAPointInTimeSurvivesSerialization()
  {
    var serializer = ComponentsForKnownTypes.GetSerializer<DateTimeOffset>();
    var instant = new DateTimeOffset(2026, 11, 15, 12, 30, 15, TimeSpan.FromMinutes(-150)).AddTicks(7);
    var read = serializer.Deserialize(serializer.Serialize(instant));
    Assert.That(read.Ticks, Is.EqualTo(instant.Ticks));
    Assert.That(read.Offset, Is.EqualTo(instant.Offset));
    // The padding is zeros, so equal values serialize alike.
    Assert.That(serializer.Serialize(instant).ToArray(), Is.EqualTo(serializer.Serialize(read).ToArray()));
  }

  [Test]
  public void ReadOnlyByteMemoryIsDeletedWhenEmpty()
  {
    var isDeleted = ComponentsForKnownTypes.GetIsDeleted<int, ReadOnlyMemory<byte>>();
    Assert.That(isDeleted(1, default), Is.True);
    Assert.That(isDeleted(1, new byte[] { 1, 2, 3 }.AsMemory(1, 0)), Is.True);
    Assert.That(isDeleted(1, new byte[] { 1 }), Is.False);
    Assert.That(ComponentsForKnownTypes.GetIsDeleted<int, TimeSpan>()(1, TimeSpan.Zero), Is.True);
    Assert.That(ComponentsForKnownTypes.GetIsDeleted<int, Status>()(1, Status.Open), Is.True);
    Assert.That(ComponentsForKnownTypes.GetIsDeleted<int, Status>()(1, Status.Closed), Is.False);
  }

  [Test]
  public void EnumComponentsRejectOtherTypes()
  {
    Assert.Throws<ArgumentException>(() => new EnumComparerAscending<int>());
    Assert.Throws<ArgumentException>(() => new EnumComparerDescending<string>());
    Assert.Throws<ArgumentException>(() => new EnumSerializer<long>());
  }

  [Test]
  public void SerializersRejectBytesShorterThanTheirType()
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => new GuidSerializer().Deserialize(new byte[15]));
    Assert.Throws<ArgumentOutOfRangeException>(() => new GuidSerializer().Deserialize(Memory<byte>.Empty));
    Assert.Throws<ArgumentOutOfRangeException>(() => new DecimalSerializer().Deserialize(new byte[15]));
    Assert.Throws<ArgumentOutOfRangeException>(() => new DecimalSerializer().Deserialize(Memory<byte>.Empty));
    Assert.Throws<ArgumentOutOfRangeException>(() => new EnumSerializer<Status>().Deserialize(new byte[Unsafe.SizeOf<Status>() - 1]));
    Assert.Throws<ArgumentOutOfRangeException>(() => new EnumSerializer<Wide>().Deserialize(new byte[Unsafe.SizeOf<Wide>() - 1]));
    Assert.Throws<ArgumentOutOfRangeException>(() => new EnumSerializer<Wide>().Deserialize(Memory<byte>.Empty));
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void TreesKeyedByEveryBuiltInTypeSortSeekAndReload(DiskSegmentMode mode)
  {
    AssertTree<sbyte>(mode, sbyte.MinValue, -1, 0, 1, 2, sbyte.MaxValue);
    AssertTree<float>(mode, float.NegativeInfinity, -1.5f, 0f, 1.5f, 2.5f, float.PositiveInfinity);
    AssertTree<Half>(mode, Half.NegativeInfinity, (Half)(-1.5), Half.Zero, (Half)1.5, (Half)2.5, Half.PositiveInfinity);
    AssertTree<nint>(mode, nint.MinValue, -1, 0, 1, 2, nint.MaxValue);
    AssertTree<nuint>(mode, 0, 1, 2, 3, 4, nuint.MaxValue);
    AssertTree<Int128>(mode, Int128.MinValue, -1, 0, 1, ulong.MaxValue, Int128.MaxValue);
    AssertTree<UInt128>(mode, 0, 1, 2, ulong.MaxValue, (UInt128)ulong.MaxValue + 1, UInt128.MaxValue);
    AssertTree<BigInteger>(mode, -BigInteger.Pow(2, 300), -1, 0, 1, ulong.MaxValue, BigInteger.Pow(2, 300));
    AssertTree<TimeSpan>(mode, TimeSpan.MinValue, TimeSpan.FromTicks(-1), TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1), TimeSpan.MaxValue);
    AssertTree<DateTimeOffset>(mode, DateTimeOffset.MinValue, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(5)),
        new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.FromHours(-3)),
        new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue);
    AssertTree<DateOnly>(mode, DateOnly.MinValue, new DateOnly(2000, 1, 1), new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 15), new DateOnly(2027, 1, 1), DateOnly.MaxValue);
    AssertTree<TimeOnly>(mode, TimeOnly.MinValue, new TimeOnly(0, 0, 1), new TimeOnly(6, 0), new TimeOnly(12, 30), new TimeOnly(18, 0), TimeOnly.MaxValue);
    AssertTree<bool>(mode, false, true);
    AssertTree<Status>(mode, Status.Draft, Status.Open, Status.Closed);
    AssertTree<Wide>(mode, Wide.Low, Wide.High);
    AssertTree<ReadOnlyMemory<byte>>(mode, Array.Empty<byte>(), new byte[] { 0 }, new byte[] { 0, 1 }, new byte[] { 1 }, new byte[] { 1, 0 }, new byte[] { 255 });
    AssertTree<char>(mode, '\0', 'A', 'ÿ', 'Ā', 'Ж', '￿');
    AssertTree<short>(mode, short.MinValue, -1, 0, 1, 2, short.MaxValue);
    AssertTree<int>(mode, int.MinValue, -1, 0, 1, 2, int.MaxValue);
    AssertTree<long>(mode, long.MinValue, -1, 0, 1, 2, long.MaxValue);
    AssertTree<double>(mode, double.NegativeInfinity, -1.5, 0, 1.5, 2.5, double.PositiveInfinity);
    AssertTree<decimal>(mode, decimal.MinValue, -1.5m, 0m, 0.001m, 1.5m, decimal.MaxValue);
    AssertTree<DateTime>(mode, DateTime.MinValue, new DateTime(2000, 1, 1), new DateTime(2026, 11, 15), new DateTime(2026, 11, 16), new DateTime(2027, 1, 1), DateTime.MaxValue);
    AssertTree<Guid>(mode, Guid.Empty, Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"),
        Guid.Parse("01000000-0000-0000-0000-000000000000"), Guid.Parse("02000000-0000-0000-0000-000000000000"), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
  }

  /// <summary>
  /// Writes keys given in order into a tree out of order, and checks their order, their seeks and their values in
  /// the mutable segment, on disk after a merge, and after a reopen.
  /// </summary>
  static void AssertTree<TKey>(DiskSegmentMode mode, params TKey[] ordered)
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = CreateFactory<TKey, int>(provider, mode).Create())
    {
      for (var i = ordered.Length - 1; i >= 0; --i)
        tree.Upsert(ordered[i], i + 1);
      AssertTreeHolds(tree, ordered);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
      AssertTreeHolds(tree, ordered);
    }
    using (var tree = CreateFactory<TKey, int>(provider, mode).Open())
      AssertTreeHolds(tree, ordered);
  }

  static void AssertTreeHolds<TKey>(IZoneTree<TKey, int> tree, TKey[] ordered)
  {
    var comparer = ComponentsForKnownTypes.GetComparer<TKey>();
    using var iterator = tree.CreateIterator();
    for (var i = 0; i < ordered.Length; ++i)
    {
      Assert.That(iterator.Next(), Is.True, typeof(TKey).Name);
      Assert.That(comparer.Compare(iterator.CurrentKey, ordered[i]), Is.Zero, $"{typeof(TKey).Name} {ordered[i]}");
      Assert.That(iterator.CurrentValue, Is.EqualTo(i + 1));
      Assert.That(tree.TryGet(ordered[i], out var value), Is.True, $"{typeof(TKey).Name} {ordered[i]}");
      Assert.That(value, Is.EqualTo(i + 1));
    }
    Assert.That(iterator.Next(), Is.False);
    using var seek = tree.CreateIterator();
    seek.Seek(ordered[^1]);
    Assert.That(seek.Next(), Is.True);
    Assert.That(seek.CurrentValue, Is.EqualTo(ordered.Length));
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void TreesValuedByEveryBuiltInTypeKeepTheirValues(DiskSegmentMode mode)
  {
    AssertValues<sbyte>(mode, -5, 7);
    AssertValues<float>(mode, -1.5f, 2.5f);
    AssertValues<Half>(mode, (Half)(-1.5), (Half)2.5);
    AssertValues<nint>(mode, -5, nint.MaxValue);
    AssertValues<nuint>(mode, 5, nuint.MaxValue);
    AssertValues<Int128>(mode, Int128.MinValue, Int128.MaxValue);
    AssertValues<UInt128>(mode, 5, UInt128.MaxValue);
    AssertValues<BigInteger>(mode, -BigInteger.Pow(2, 300), BigInteger.Pow(2, 300));
    AssertValues<TimeSpan>(mode, TimeSpan.FromTicks(-5), TimeSpan.MaxValue);
    AssertValues<DateTimeOffset>(mode, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(5)), DateTimeOffset.MaxValue);
    AssertValues<DateOnly>(mode, new DateOnly(2026, 11, 15), DateOnly.MaxValue);
    AssertValues<TimeOnly>(mode, new TimeOnly(12, 30), TimeOnly.MaxValue);
    AssertValues<bool>(mode, true, true);
    AssertValues<Status>(mode, Status.Draft, Status.Closed);
    AssertValues<ReadOnlyMemory<byte>>(mode, new byte[] { 0, 1 }, new byte[] { 255 });
    AssertValues<char>(mode, 'Ж', '￿');
    AssertValues<decimal>(mode, -1.5m, decimal.MaxValue);
    AssertValues<Guid>(mode, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
    AssertValues<double>(mode, -1.5, double.MaxValue);
  }

  /// <summary>Writes values that are not deletions and checks them in each segment and after a reopen.</summary>
  static void AssertValues<TValue>(DiskSegmentMode mode, params TValue[] values)
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = CreateFactory<int, TValue>(provider, mode).Create())
    {
      for (var i = 0; i < values.Length; ++i)
        tree.Upsert(i, values[i]);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
      AssertHolds(tree, values);
    }
    using (var tree = CreateFactory<int, TValue>(provider, mode).Open())
      AssertHolds(tree, values);
  }

  static void AssertHolds<TValue>(IZoneTree<int, TValue> tree, TValue[] values)
  {
    for (var i = 0; i < values.Length; ++i)
    {
      Assert.That(tree.TryGet(i, out var value), Is.True, typeof(TValue).Name);
      if (value is ReadOnlyMemory<byte> bytes)
        Assert.That(bytes.ToArray(), Is.EqualTo(((ReadOnlyMemory<byte>)(object)values[i]).ToArray()));
      else if (value is DateTimeOffset instant)
        Assert.That((instant.Ticks, instant.Offset), Is.EqualTo((((DateTimeOffset)(object)values[i]).Ticks, ((DateTimeOffset)(object)values[i]).Offset)));
      else
        Assert.That(value, Is.EqualTo(values[i]), typeof(TValue).Name);
    }
  }

  static ZoneTreeFactory<TKey, TValue> CreateFactory<TKey, TValue>(InMemoryFileStreamProvider provider, DiskSegmentMode mode)
  {
    return new ZoneTreeFactory<TKey, TValue>(provider)
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
