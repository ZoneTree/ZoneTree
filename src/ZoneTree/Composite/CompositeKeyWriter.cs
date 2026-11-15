using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using static ZoneTree.Composite.CompositeKeyFormat;

namespace ZoneTree.Composite;

/// <summary>
/// Writes composite key elements into an array at an offset, or measures them: a key is measured first, so it
/// is written into an array of its exact length.
/// </summary>
internal struct CompositeKeyWriter : IElementSink
{
  readonly byte[] destination;
  readonly int start;
  readonly bool measuring;
  int length;

  /// <summary>A writer that measures the elements it is given.</summary>
  public CompositeKeyWriter()
  {
    destination = null;
    start = 0;
    measuring = true;
    length = 0;
  }

  /// <summary>A writer into an array with room for the measured elements from an offset.</summary>
  internal CompositeKeyWriter(byte[] destination, int start)
  {
    this.destination = destination;
    this.start = start;
    measuring = false;
    length = 0;
  }

  internal readonly int Length => length;

  /// <exception cref="NotSupportedException">Composite keys do not support the type.</exception>
  /// <exception cref="ArgumentException">A string is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A <see cref="BigInteger"/> is too large for an element.</exception>
  internal void Append<T>(in T value) => Append(value, descending: false);

  /// <summary>Appends an element, descending if its type is a <see cref="Descending{T}"/> or if asked.</summary>
  /// <exception cref="NotSupportedException">Composite keys do not support the type.</exception>
  /// <exception cref="ArgumentException">A string is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A <see cref="BigInteger"/> is too large for an element.</exception>
  internal void Append<T>(in T value, bool descending)
  {
    var shape = ShapeOf<T>.Value;
    var begin = length;
    CompositeElementWriting.Write(ref Unsafe.AsRef(in value), ref this);
    // A descending element is its ascending bytes complemented, tag included.
    if ((shape.IsDescending || descending) && !measuring)
      Complement(begin);
  }

  /// <summary>The destination from the end of what is written so far.</summary>
  readonly Span<byte> Rest => destination.AsSpan(start + length);

  /// <summary>Complements what is written from a point to the end.</summary>
  readonly void Complement(int from)
  {
    foreach (ref var b in destination.AsSpan(start + from, length - from))
      b = (byte)~b;
  }

  public void WriteNull() => Tag(Null);

  public void WriteBoolean(bool value) => Tag(value ? True : False);

  public void WriteSigned(long value)
  {
    if (value >= 0)
      WriteUnsigned((ulong)value);
    else
      // The magnitude of long.MinValue is 2^63, which an unsigned integer holds.
      Negative((ulong)(-(value + 1)) + 1);
  }

  public void WriteUnsigned(ulong value)
  {
    if (value == 0)
    {
      Tag(IntegerZero);
      return;
    }
    var width = Width(value);
    Tag((byte)(IntegerZero + width));
    BigEndian(value, width);
  }

#if NET7_0_OR_GREATER
  public void WriteSigned(Int128 value)
  {
    if (Int128.IsNegative(value))
      Integer(negative: true, (UInt128)(-(value + 1)) + 1);
    else
      Integer(negative: false, (UInt128)value);
  }

  public void WriteUnsigned(UInt128 value) => Integer(negative: false, value);

  void Integer(bool negative, UInt128 magnitude)
  {
    if (magnitude <= ulong.MaxValue)
    {
      if (negative)
        Negative((ulong)magnitude);
      else
        WriteUnsigned((ulong)magnitude);
      return;
    }
    Span<byte> bytes = stackalloc byte[16];
    BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)(magnitude >> 64));
    BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(8), (ulong)magnitude);
    Wide(negative, bytes.Slice(bytes.IndexOfAnyExcept((byte)0)));
  }
#endif

  public void WriteInteger(BigInteger value)
  {
    var negative = value.Sign < 0;
    var magnitude = BigInteger.Abs(value);
    if (magnitude <= ulong.MaxValue)
    {
      if (negative)
        Negative((ulong)magnitude);
      else
        WriteUnsigned((ulong)magnitude);
      return;
    }
    var count = magnitude.GetByteCount(isUnsigned: true);
    if (count > MaximumWideWidth)
      throw new ArgumentOutOfRangeException(nameof(value), $"A composite key integer holds at most {MaximumWideWidth} bytes, and the value has {count}.");
    Span<byte> bytes = stackalloc byte[MaximumWideWidth];
    magnitude.TryWriteBytes(bytes, out var written, isUnsigned: true, isBigEndian: true);
    Wide(negative, bytes.Slice(0, written));
  }

  public void WriteFloating(Half value) => Fixed(Floating, OrderedFloating((double)value));

  public void WriteFloating(float value) => Fixed(Floating, OrderedFloating(value));

  public void WriteFloating(double value) => Fixed(Floating, OrderedFloating(value));

  public void WriteDecimal(decimal value)
  {
    Span<int> bits = stackalloc int[4];
    decimal.GetBits(value, bits);
    uint low = (uint)bits[0], middle = (uint)bits[1], high = (uint)bits[2];
    if ((low | middle | high) == 0)
    {
      Tag(DecimalZero);
      return;
    }
    var scale = (bits[3] >> 16) & 0xFF;
    var negative = bits[3] < 0;
    // The digits, least significant first.
    Span<byte> digits = stackalloc byte[MaximumDecimalDigits];
    var count = 0;
    while ((low | middle | high) != 0)
      digits[count++] = DivideByTen(ref low, ref middle, ref high);
    var trailing = 0;
    while (digits[trailing] == 0)
      ++trailing;
    Tag(negative ? NegativeDecimal : PositiveDecimal);
    var payload = length;
    // The exponent places the decimal point before the first digit.
    Payload((byte)(count - scale + DecimalExponentBias));
    for (var i = count - 1; i >= trailing; --i)
      Payload((byte)('0' + digits[i]));
    Payload(0);
    // A negative decimal holds its magnitude complemented, so larger magnitudes sort first.
    if (negative && !measuring)
      Complement(payload);
  }

  public void WriteDateTime(DateTime value) => Fixed(DateTimeTag, (ulong)value.Ticks);

  public void WriteDateTimeOffset(DateTimeOffset value) => Fixed(DateTimeOffsetTag, (ulong)value.UtcTicks);

  public void WriteDateOnly(DateOnly value)
  {
    Tag(DateOnlyTag);
    if (!measuring)
      BinaryPrimitives.WriteUInt32BigEndian(Rest, (uint)value.DayNumber);
    length += 4;
  }

  public void WriteTimeOnly(TimeOnly value) => Fixed(TimeOnlyTag, (ulong)value.Ticks);

  // Flipping the sign bit orders signed ticks as unsigned bytes.
  public void WriteTimeSpan(TimeSpan value) => Fixed(TimeSpanTag, (ulong)value.Ticks ^ SignBit);

  public void WriteGuid(Guid value)
  {
    Tag(GuidTag);
    if (!measuring)
      CompositeKeyFormat.WriteGuid(value, Rest.Slice(0, 16));
    length += 16;
  }

  public void WriteString(string value)
  {
    Tag(StringTag);
    if (measuring)
    {
      // A string's UTF-8 bytes hold a zero byte for each U+0000 it holds, and each needs an escape.
      length += StrictUtf8.GetByteCount(value) + Count(value.AsSpan(), '\0');
    }
    else
    {
      // The string is encoded in place, and its zero bytes escaped from the end, so its bytes are not copied.
      var rest = Rest;
      var count = StrictUtf8.GetBytes(value, rest);
      var zeros = Count(rest.Slice(0, count), (byte)0);
      if (zeros > 0)
        EscapeInPlace(rest.Slice(0, count + zeros), count);
      length += count + zeros;
    }
    Terminate();
  }

  public void WriteBytes(ReadOnlySpan<byte> value)
  {
    Tag(BytesTag);
    if (measuring)
      length += value.Length + Count(value, (byte)0);
    else
    {
      // The bytes are copied a run at a time, each zero byte followed by its escape.
      while (true)
      {
        var zero = value.IndexOf((byte)0);
        if (zero < 0)
        {
          value.CopyTo(Rest);
          length += value.Length;
          break;
        }
        value.Slice(0, zero + 1).CopyTo(Rest);
        length += zero + 1;
        Payload(Escape);
        value = value.Slice(zero + 1);
      }
    }
    Terminate();
  }

  void Tag(byte tag) => Payload(tag);

  void Payload(byte value)
  {
    if (!measuring)
      destination[start + length] = value;
    ++length;
  }

  void Negative(ulong magnitude)
  {
    var width = Width(magnitude);
    Tag((byte)(IntegerZero - width));
    BigEndian(~magnitude, width);
  }

  static int Width(ulong value) => (64 - BitOperations.LeadingZeroCount(value) + 7) / 8;

  void BigEndian(ulong value, int width)
  {
    if (!measuring)
    {
      var rest = Rest;
      for (var i = 0; i < width; ++i)
        rest[i] = (byte)(value >> (8 * (width - 1 - i)));
    }
    length += width;
  }

  /// <summary>Writes an integer wider than 8 bytes from its big-endian magnitude.</summary>
  void Wide(bool negative, scoped ReadOnlySpan<byte> magnitude)
  {
    Tag(negative ? WideNegativeInteger : WidePositiveInteger);
    var payload = length;
    Payload((byte)magnitude.Length);
    if (!measuring)
      magnitude.CopyTo(Rest);
    length += magnitude.Length;
    // A negative integer holds its length and magnitude complemented, so larger magnitudes sort first.
    if (negative && !measuring)
      Complement(payload);
  }

  void Fixed(byte tag, ulong value)
  {
    Tag(tag);
    if (!measuring)
      BinaryPrimitives.WriteUInt64BigEndian(Rest, value);
    length += 8;
  }

  void Terminate()
  {
    Payload(0);
    Payload(Terminator);
  }

  static int Count<TItem>(ReadOnlySpan<TItem> value, TItem item) where TItem : IEquatable<TItem>
  {
#if NET8_0_OR_GREATER
    return value.Count(item);
#else
    var count = 0;
    while (true)
    {
      var found = value.IndexOf(item);
      if (found < 0)
        return count;
      ++count;
      value = value.Slice(found + 1);
    }
#endif
  }

  /// <summary>Escapes the zero bytes of the first <paramref name="count"/> bytes, working back from the end.</summary>
  static void EscapeInPlace(Span<byte> region, int count)
  {
    var write = region.Length - 1;
    for (var read = count - 1; read >= 0; --read)
    {
      var value = region[read];
      if (value == 0)
        region[write--] = Escape;
      region[write--] = value;
    }
  }
}
