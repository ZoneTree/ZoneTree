using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ZoneTree.Composite.CompositeKeyFormat;

namespace ZoneTree.Composite;

/// <summary>
/// The byte format of composite values. A value starts with a header: a byte that gives the width of the header's
/// numbers, 1, 2 or 4 bytes, then the element count, a type byte for each element, and where each element's
/// payload ends. The payloads follow, each in the bytes of its type: strings as UTF-8 and bytes as they are, with
/// nothing escaped, and numbers little-endian. Any element is reached from the header without reading the others.
/// </summary>
internal static class CompositeValueFormat
{
  /// <summary>The header byte holds the width code in its low two bits; the other bits are zero.</summary>
  internal const int MaximumWidthCode = 2;

  internal const ulong KindShift = 62;
  internal const ulong TicksMask = (1UL << 62) - 1;

  internal static int WidthCode(int largest) => largest <= byte.MaxValue ? 0 : largest <= ushort.MaxValue ? 1 : 2;

  internal static int HeaderLength(int width, int count) => 1 + width + count * (1 + width);

  internal static int ReadNumber(ReadOnlySpan<byte> span, int width) => width switch
  {
    1 => span[0],
    2 => BinaryPrimitives.ReadUInt16LittleEndian(span),
    _ => BinaryPrimitives.ReadInt32LittleEndian(span)
  };

  internal static void WriteNumber(Span<byte> span, int width, int number)
  {
    switch (width)
    {
      case 1:
        span[0] = (byte)number;
        break;
      case 2:
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)number);
        break;
      default:
        BinaryPrimitives.WriteInt32LittleEndian(span, number);
        break;
    }
  }

  /// <summary>An array for a value of these elements, with its header byte and count written.</summary>
  internal static byte[] Allocate(int count, int payloadLength, out int width)
  {
    var code = WidthCode(Math.Max(count, payloadLength));
    width = 1 << code;
    var value = new byte[HeaderLength(width, count) + payloadLength];
    value[0] = (byte)code;
    WriteNumber(value.AsSpan(1), width, count);
    return value;
  }

  internal static ArgumentException Malformed()
      => new("The bytes are not a composite value: their header is malformed.");

  internal static ArgumentException Malformed(int index)
      => new($"The bytes are not a composite value: element {index} is malformed.");

  /// <summary>The minimal two's complement little-endian width of a signed integer: none for zero.</summary>
  internal static int SignedWidth(long value)
      => value == 0 ? 0 : (64 - BitOperations.LeadingZeroCount((ulong)(value ^ (value >> 63))) + 8) / 8;

  /// <summary>Reads an integer of at most 8 bytes, sign-extended.</summary>
  internal static bool TryNarrow(ReadOnlySpan<byte> payload, out long value)
  {
    switch (payload.Length)
    {
      case 0:
        value = 0;
        return true;
      case 8:
        value = BinaryPrimitives.ReadInt64LittleEndian(payload);
        return true;
      case > 8:
        value = 0;
        return false;
    }
    ulong raw = 0;
    for (var i = payload.Length - 1; i >= 0; --i)
      raw = (raw << 8) | payload[i];
    var shift = 64 - 8 * payload.Length;
    value = (long)(raw << shift) >> shift;
    return true;
  }

  /// <summary>Reads a nonnegative integer above <see cref="long.MaxValue"/> and at most <see cref="ulong.MaxValue"/>.</summary>
  internal static bool TryLargeUnsigned(ReadOnlySpan<byte> payload, out ulong value)
  {
    value = 0;
    if (payload.Length != 9 || payload[8] != 0)
      return false;
    value = BinaryPrimitives.ReadUInt64LittleEndian(payload);
    return true;
  }

  internal static BigInteger IntegerValue(ReadOnlySpan<byte> payload)
      => payload.Length == 0 ? BigInteger.Zero : new BigInteger(payload, isUnsigned: false, isBigEndian: false);

  internal static double FloatingValue(ReadOnlySpan<byte> payload) => payload.Length switch
  {
    2 => (double)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(payload)),
    4 => BinaryPrimitives.ReadSingleLittleEndian(payload),
    _ => BinaryPrimitives.ReadDoubleLittleEndian(payload)
  };

  internal static decimal DecimalValue(ReadOnlySpan<byte> payload)
  {
    if (BitConverter.IsLittleEndian)
      return Unsafe.ReadUnaligned<decimal>(ref MemoryMarshal.GetReference(payload));
    var flags = BinaryPrimitives.ReadInt32LittleEndian(payload);
    return new decimal(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8)),
        BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12)),
        BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4)), flags < 0, (byte)(flags >> 16));
  }

  /// <summary>Whether bytes are valid UTF-8.</summary>
  static bool IsValidText(ReadOnlySpan<byte> payload)
  {
#if NET8_0_OR_GREATER
    return System.Text.Unicode.Utf8.IsValid(payload);
#else
    try
    {
      StrictUtf8.GetCharCount(payload);
      return true;
    }
    catch (System.Text.DecoderFallbackException)
    {
      return false;
    }
#endif
  }

  /// <summary>Whether a decimal's sign and scale are a decimal's: a scale up to 28, and no other bits.</summary>
  static bool IsDecimal(ReadOnlySpan<byte> payload)
  {
    var flags = BinaryPrimitives.ReadInt32LittleEndian(payload);
    return (flags & 0x7F00FFFF) == 0 && ((flags >> 16) & 0xFF) <= 28;
  }

  /// <summary>
  /// Whether an element's payload is one of its type: the type's size and range, and valid UTF-8 for a string when
  /// asked.
  /// </summary>
  internal static bool IsValidElement(byte tag, ReadOnlySpan<byte> payload, bool checkText)
  {
    switch ((CompositeElementType)tag)
    {
      case CompositeElementType.Null:
        return payload.Length == 0;
      case CompositeElementType.Bytes:
      case CompositeElementType.Integer:
        return true;
      case CompositeElementType.String:
        return !checkText || IsValidText(payload);
      case CompositeElementType.Floating:
        return payload.Length is 2 or 4 or 8;
      case CompositeElementType.Decimal:
        return payload.Length == 16 && IsDecimal(payload);
      case CompositeElementType.Boolean:
        return payload.Length == 1 && payload[0] <= 1;
      case CompositeElementType.DateTime:
        if (payload.Length != 8)
          return false;
        var data = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        return data >> (int)KindShift != 3 && (data & TicksMask) <= (ulong)DateTime.MaxValue.Ticks;
      case CompositeElementType.DateTimeOffset:
        if (payload.Length != 10)
          return false;
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(payload);
        var minutes = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(8));
        var utc = ticks - minutes * TimeSpan.TicksPerMinute;
        // The offset is compared as it is: the absolute value of short.MinValue would overflow.
        return ticks >= 0 && ticks <= DateTime.MaxValue.Ticks && minutes >= -14 * 60 && minutes <= 14 * 60 &&
            utc >= 0 && utc <= DateTime.MaxValue.Ticks;
      case CompositeElementType.DateOnly:
        return payload.Length == 4 && (uint)BinaryPrimitives.ReadInt32LittleEndian(payload) <= (uint)DateOnly.MaxValue.DayNumber;
      case CompositeElementType.TimeOnly:
        return payload.Length == 8 && (ulong)BinaryPrimitives.ReadInt64LittleEndian(payload) <= (ulong)TimeOnly.MaxValue.Ticks;
      case CompositeElementType.TimeSpan:
        return payload.Length == 8;
      case CompositeElementType.Guid:
        return payload.Length == 16;
      default:
        return false;
    }
  }
}

/// <summary>The header of a composite value, read once, and its elements by index.</summary>
internal readonly ref struct CompositeValueLayout
{
  readonly ReadOnlySpan<byte> value;
  readonly int width;
  readonly int payloadStart;

  CompositeValueLayout(ReadOnlySpan<byte> value, int width, int count, int payloadStart)
  {
    this.value = value;
    this.width = width;
    Count = count;
    this.payloadStart = payloadStart;
  }

  internal int Count { get; }

  internal int PayloadLength => value.Length - payloadStart;

  /// <summary>Reads a header, checked to fit the bytes and to end where the last element ends.</summary>
  /// <exception cref="ArgumentException">The header is malformed.</exception>
  internal static CompositeValueLayout Parse(ReadOnlySpan<byte> value)
      => TryParse(value, out var layout) ? layout : throw CompositeValueFormat.Malformed();

  internal static bool TryParse(ReadOnlySpan<byte> value, out CompositeValueLayout layout)
  {
    layout = default;
    if (value.Length < 2 || value[0] > CompositeValueFormat.MaximumWidthCode)
      return false;
    var width = 1 << value[0];
    if (value.Length < 1 + width)
      return false;
    var count = CompositeValueFormat.ReadNumber(value.Slice(1), width);
    if (count < 0 || (long)count * (1 + width) > value.Length - 1 - width)
      return false;
    layout = new CompositeValueLayout(value, width, count, CompositeValueFormat.HeaderLength(width, count));
    return (count == 0 ? 0 : layout.End(count - 1)) == layout.PayloadLength;
  }

  internal byte Tag(int index) => value[1 + width + index];

  /// <summary>Where an element's payload ends, counted from the first payload.</summary>
  internal int End(int index) => CompositeValueFormat.ReadNumber(value.Slice(1 + width + Count + index * width), width);

  /// <summary>Every element's payload, one after another.</summary>
  internal ReadOnlySpan<byte> Payloads => value.Slice(payloadStart);

  /// <summary>Every element's type byte.</summary>
  internal ReadOnlySpan<byte> Tags => value.Slice(1 + width, Count);

  /// <summary>An element's payload, checked to lie within the payloads.</summary>
  /// <exception cref="ArgumentException">The element's bounds are malformed.</exception>
  internal ReadOnlySpan<byte> Payload(int index)
      => TryPayload(index, out var payload) ? payload : throw CompositeValueFormat.Malformed(index);

  internal bool TryPayload(int index, out ReadOnlySpan<byte> payload)
  {
    var start = index == 0 ? 0 : End(index - 1);
    var end = End(index);
    if (start < 0 || start > end || end > PayloadLength)
    {
      payload = default;
      return false;
    }
    payload = value.Slice(payloadStart + start, end - start);
    return true;
  }

  /// <exception cref="ArgumentOutOfRangeException">The value has no element at the index.</exception>
  internal void CheckIndex(int index)
  {
    if ((uint)index >= (uint)Count)
      throw new ArgumentOutOfRangeException(nameof(index), index, $"The value has {Count} elements.");
  }
}
