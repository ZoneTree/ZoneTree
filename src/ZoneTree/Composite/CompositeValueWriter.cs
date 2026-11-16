using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ZoneTree.Composite.CompositeValueFormat;

namespace ZoneTree.Composite;

/// <summary>
/// Writes the elements of a composite value, or measures them: a value is measured first, so it is written into
/// an array of its exact length. A writer of payloads only writes elements' payloads into a buffer, for a
/// builder that writes the header itself.
/// </summary>
internal struct CompositeValueWriter : IElementSink
{
  readonly byte[] buffer;
  readonly int width;
  readonly int count;
  readonly int payloadStart;
  readonly bool measuring;
  readonly bool payloadsOnly;
  int index;
  int payloadLength;
  CompositeElementType written;

  /// <summary>A writer that measures the elements it is given.</summary>
  public CompositeValueWriter()
  {
    buffer = null;
    width = 0;
    count = 0;
    payloadStart = 0;
    measuring = true;
    payloadsOnly = false;
    index = 0;
    payloadLength = 0;
    written = CompositeElementType.Null;
  }

  /// <summary>A writer into an array <see cref="Allocate"/> made for the measured elements.</summary>
  internal CompositeValueWriter(byte[] buffer, int width, int count)
      : this(buffer, width, count, HeaderLength(width, count), payloadsOnly: false)
  {
  }

  CompositeValueWriter(byte[] buffer, int width, int count, int payloadStart, bool payloadsOnly)
  {
    this.buffer = buffer;
    this.width = width;
    this.count = count;
    this.payloadStart = payloadStart;
    measuring = false;
    this.payloadsOnly = payloadsOnly;
    index = 0;
    payloadLength = 0;
    written = CompositeElementType.Null;
  }

  /// <summary>A writer of elements' payloads only, into a buffer with room for them from an offset.</summary>
  internal static CompositeValueWriter PayloadsInto(byte[] buffer, int offset)
      => new(buffer, 0, 0, offset, payloadsOnly: true);

  internal readonly int Count => index;

  internal readonly int PayloadLength => payloadLength;

  /// <summary>The type of the element written last.</summary>
  internal readonly CompositeElementType LastType => written;

  /// <exception cref="NotSupportedException">Composite values do not support the type, or it is descending.</exception>
  /// <exception cref="ArgumentException">A string is not valid UTF-16.</exception>
  internal void Append<T>(in T element)
  {
    var shape = ShapeOf<T>.Value;
    if (shape.IsDescending)
      throw new NotSupportedException("A composite value element has no order, so it cannot be descending.");
    CompositeElementWriting.Write(ref Unsafe.AsRef(in element), ref this);
    if (!measuring && !payloadsOnly)
    {
      buffer[1 + width + index] = (byte)written;
      WriteNumber(buffer.AsSpan(1 + width + count + index * width), width, payloadLength);
    }
    ++index;
  }

  /// <summary>Records an element of a type with a payload of a number of bytes, and gives where to write it.</summary>
  Span<byte> Element(CompositeElementType type, int length)
  {
    written = type;
    var payload = measuring ? default : buffer.AsSpan(payloadStart + payloadLength, length);
    payloadLength += length;
    return payload;
  }

  public void WriteNull() => Element(CompositeElementType.Null, 0);

  public void WriteBoolean(bool value)
  {
    var payload = Element(CompositeElementType.Boolean, 1);
    if (!measuring)
      payload[0] = value ? (byte)1 : (byte)0;
  }

  /// <summary>Writes an integer in its minimal two's complement little-endian bytes, none for zero.</summary>
  public void WriteSigned(long value)
  {
    var length = SignedWidth(value);
    var payload = Element(CompositeElementType.Integer, length);
    if (!measuring)
    {
      for (var i = 0; i < length; ++i)
        payload[i] = (byte)(value >> (8 * i));
    }
  }

  public void WriteUnsigned(ulong value)
  {
    if (value <= long.MaxValue)
    {
      WriteSigned((long)value);
      return;
    }
    // Above long.MaxValue, two's complement needs a zero byte after the 8 bytes.
    var payload = Element(CompositeElementType.Integer, 9);
    if (!measuring)
    {
      BinaryPrimitives.WriteUInt64LittleEndian(payload, value);
      payload[8] = 0;
    }
  }

#if NET7_0_OR_GREATER
  public void WriteSigned(Int128 value)
  {
    if (value >= long.MinValue && value <= long.MaxValue)
    {
      WriteSigned((long)value);
      return;
    }
    var length = (128 - (int)UInt128.LeadingZeroCount((UInt128)(value ^ (value >> 127))) + 8) / 8;
    var payload = Element(CompositeElementType.Integer, length);
    if (!measuring)
    {
      Span<byte> bytes = stackalloc byte[16];
      BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)value);
      BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(8), (ulong)(value >>> 64));
      bytes.Slice(0, length).CopyTo(payload);
    }
  }

  public void WriteUnsigned(UInt128 value)
  {
    if (value <= (UInt128)Int128.MaxValue)
    {
      WriteSigned((Int128)value);
      return;
    }
    // Above Int128.MaxValue, two's complement needs a zero byte after the 16 bytes.
    var payload = Element(CompositeElementType.Integer, 17);
    if (!measuring)
    {
      BinaryPrimitives.WriteUInt64LittleEndian(payload, (ulong)value);
      BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(8), (ulong)(value >> 64));
      payload[16] = 0;
    }
  }
#endif

  public void WriteInteger(BigInteger value)
  {
    if (value >= long.MinValue && value <= long.MaxValue)
    {
      WriteSigned((long)value);
      return;
    }
    var payload = Element(CompositeElementType.Integer, value.GetByteCount(isUnsigned: false));
    if (!measuring)
      value.TryWriteBytes(payload, out _, isUnsigned: false, isBigEndian: false);
  }

  public void WriteFloating(Half value)
  {
    var payload = Element(CompositeElementType.Floating, 2);
    if (!measuring)
      BinaryPrimitives.WriteInt16LittleEndian(payload, BitConverter.HalfToInt16Bits(value));
  }

  public void WriteFloating(float value)
  {
    var payload = Element(CompositeElementType.Floating, 4);
    if (!measuring)
      BinaryPrimitives.WriteSingleLittleEndian(payload, value);
  }

  public void WriteFloating(double value)
  {
    var payload = Element(CompositeElementType.Floating, 8);
    if (!measuring)
      BinaryPrimitives.WriteDoubleLittleEndian(payload, value);
  }

  /// <summary>Writes a decimal's 16 bytes, little-endian: its sign and scale, then its coefficient's high and low bits.</summary>
  public void WriteDecimal(decimal value)
  {
    var payload = Element(CompositeElementType.Decimal, 16);
    if (measuring)
      return;
    if (BitConverter.IsLittleEndian)
    {
      Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(payload), value);
      return;
    }
    Span<int> bits = stackalloc int[4];
    decimal.GetBits(value, bits);
    BinaryPrimitives.WriteInt32LittleEndian(payload, bits[3]);
    BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(4), bits[2]);
    BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(8), bits[0]);
    BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(12), bits[1]);
  }

  public void WriteDateTime(DateTime value)
  {
    var payload = Element(CompositeElementType.DateTime, 8);
    if (!measuring)
      BinaryPrimitives.WriteUInt64LittleEndian(payload, (ulong)value.Ticks | ((ulong)value.Kind << (int)KindShift));
  }

  public void WriteDateTimeOffset(DateTimeOffset value)
  {
    var payload = Element(CompositeElementType.DateTimeOffset, 10);
    if (!measuring)
    {
      BinaryPrimitives.WriteInt64LittleEndian(payload, value.Ticks);
      BinaryPrimitives.WriteInt16LittleEndian(payload.Slice(8), (short)value.Offset.TotalMinutes);
    }
  }

  public void WriteDateOnly(DateOnly value)
  {
    var payload = Element(CompositeElementType.DateOnly, 4);
    if (!measuring)
      BinaryPrimitives.WriteInt32LittleEndian(payload, value.DayNumber);
  }

  public void WriteTimeOnly(TimeOnly value)
  {
    var payload = Element(CompositeElementType.TimeOnly, 8);
    if (!measuring)
      BinaryPrimitives.WriteInt64LittleEndian(payload, value.Ticks);
  }

  public void WriteTimeSpan(TimeSpan value)
  {
    var payload = Element(CompositeElementType.TimeSpan, 8);
    if (!measuring)
      BinaryPrimitives.WriteInt64LittleEndian(payload, value.Ticks);
  }

  public void WriteGuid(Guid value)
  {
    var payload = Element(CompositeElementType.Guid, 16);
    if (!measuring)
      value.TryWriteBytes(payload);
  }

  public void WriteString(string value)
  {
    if (measuring)
    {
      Element(CompositeElementType.String, CompositeKeyFormat.Utf8Length(value, "A string element"));
      return;
    }
    // The string is encoded straight into its payload, whose length the encoding gives.
    written = CompositeElementType.String;
    payloadLength += CompositeKeyFormat.StrictUtf8.GetBytes(value, buffer.AsSpan(payloadStart + payloadLength));
  }

  public void WriteBytes(ReadOnlySpan<byte> value)
  {
    var payload = Element(CompositeElementType.Bytes, value.Length);
    if (!measuring)
      value.CopyTo(payload);
  }
}
