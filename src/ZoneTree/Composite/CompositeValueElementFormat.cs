using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using static ZoneTree.Composite.CompositeValueFormat;

namespace ZoneTree.Composite;

/// <summary>
/// Reads the payloads of composite value elements, each in the bytes of its type, checked to be one of its type
/// before they are read.
/// </summary>
internal readonly struct CompositeValueElementFormat : IElementFormat
{
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool TryReadNarrow(ReadOnlySpan<byte> payload, in ElementInfo element, out bool negative, out ulong magnitude)
  {
    if (TryNarrow(payload, out var number))
    {
      negative = number < 0;
      // The magnitude of long.MinValue is 2^63, which an unsigned integer holds.
      magnitude = negative ? (ulong)(-(number + 1)) + 1 : (ulong)number;
      return true;
    }
    negative = false;
    return TryLargeUnsigned(payload, out magnitude);
  }

#if NET7_0_OR_GREATER
  public bool TryReadWide(ReadOnlySpan<byte> payload, in ElementInfo element, out bool negative, out UInt128 magnitude)
  {
    if (payload.Length <= 16)
    {
      var number = ReadInt128(payload);
      negative = Int128.IsNegative(number);
      magnitude = negative ? (UInt128)(-(number + 1)) + 1 : (UInt128)number;
      return true;
    }
    negative = false;
    // Above Int128.MaxValue, two's complement needs a zero byte after the 16 bytes.
    if (payload.Length == 17 && payload[16] == 0)
    {
      magnitude = new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8)), BinaryPrimitives.ReadUInt64LittleEndian(payload));
      return true;
    }
    magnitude = 0;
    return false;
  }

  /// <summary>Reads an integer of at most 16 bytes, sign-extended.</summary>
  static Int128 ReadInt128(ReadOnlySpan<byte> payload)
  {
    if (TryNarrow(payload, out var narrow))
      return narrow;
    Span<byte> bytes = stackalloc byte[16];
    bytes.Fill((payload[^1] & 0x80) != 0 ? (byte)0xFF : (byte)0);
    payload.CopyTo(bytes);
    return new Int128(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8)), BinaryPrimitives.ReadUInt64LittleEndian(bytes));
  }
#endif

  public BigInteger ReadInteger(ReadOnlySpan<byte> payload, in ElementInfo element) => IntegerValue(payload);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public double ReadFloating(ReadOnlySpan<byte> payload, in ElementInfo element) => FloatingValue(payload);

  public bool TryReadHalf(ReadOnlySpan<byte> payload, in ElementInfo element, out Half value)
  {
    if (payload.Length != 2)
    {
      value = default;
      return false;
    }
    value = BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(payload));
    return true;
  }

  public bool TryReadSingle(ReadOnlySpan<byte> payload, in ElementInfo element, out float value)
  {
    if (payload.Length != 4)
    {
      value = 0;
      return false;
    }
    value = BinaryPrimitives.ReadSingleLittleEndian(payload);
    return true;
  }

  public decimal ReadDecimal(ReadOnlySpan<byte> payload, in ElementInfo element) => DecimalValue(payload);

  public bool ReadBoolean(ReadOnlySpan<byte> payload, in ElementInfo element) => payload[0] == 1;

  public DateTime ReadDateTime(ReadOnlySpan<byte> payload, in ElementInfo element)
  {
    var data = BinaryPrimitives.ReadUInt64LittleEndian(payload);
    return new DateTime((long)(data & TicksMask), (DateTimeKind)(data >> (int)KindShift));
  }

  public DateTimeOffset ReadDateTimeOffset(ReadOnlySpan<byte> payload, in ElementInfo element)
      => new(BinaryPrimitives.ReadInt64LittleEndian(payload), TimeSpan.FromMinutes(BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(8))));

  public DateOnly ReadDateOnly(ReadOnlySpan<byte> payload, in ElementInfo element)
      => DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(payload));

  public TimeOnly ReadTimeOnly(ReadOnlySpan<byte> payload, in ElementInfo element)
      => new(BinaryPrimitives.ReadInt64LittleEndian(payload));

  public TimeSpan ReadTimeSpan(ReadOnlySpan<byte> payload, in ElementInfo element)
      => new(BinaryPrimitives.ReadInt64LittleEndian(payload));

  public Guid ReadGuid(ReadOnlySpan<byte> payload, in ElementInfo element) => new(payload);

  public string ReadString(ReadOnlySpan<byte> payload, in ElementInfo element)
      => CompositeKeyFormat.StrictUtf8.GetString(payload);

  public byte[] ReadBytes(ReadOnlySpan<byte> payload, in ElementInfo element) => payload.ToArray();
}
