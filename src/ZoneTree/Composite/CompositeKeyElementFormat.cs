using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using static ZoneTree.Composite.CompositeKeyFormat;

namespace ZoneTree.Composite;

/// <summary>
/// Reads the payloads of composite key elements: the bytes after an element's tag, through the element's mask,
/// checked to be in canonical form before they are read.
/// </summary>
internal readonly struct CompositeKeyElementFormat : IElementFormat
{
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool TryReadNarrow(ReadOnlySpan<byte> payload, in ElementInfo element, out bool negative, out ulong magnitude)
  {
    var tag = element.Tag;
    negative = tag < IntegerZero;
    if (tag == WideNegativeInteger || tag == WidePositiveInteger)
    {
      magnitude = 0;
      return false;
    }
    magnitude = NarrowMagnitude(tag, payload, element.Mask);
    return true;
  }

#if NET7_0_OR_GREATER
  public bool TryReadWide(ReadOnlySpan<byte> payload, in ElementInfo element, out bool negative, out UInt128 magnitude)
  {
    var tag = element.Tag;
    negative = tag < IntegerZero;
    if (tag != WideNegativeInteger && tag != WidePositiveInteger)
    {
      magnitude = NarrowMagnitude(tag, payload, element.Mask);
      return true;
    }
    Span<byte> buffer = stackalloc byte[MaximumWideWidth];
    var bytes = WideMagnitude(tag, payload, element.Mask, buffer);
    if (bytes.Length > 16)
    {
      magnitude = 0;
      return false;
    }
    Span<byte> padded = stackalloc byte[16];
    padded.Clear();
    bytes.CopyTo(padded.Slice(16 - bytes.Length));
    magnitude = ((UInt128)BinaryPrimitives.ReadUInt64BigEndian(padded) << 64) | BinaryPrimitives.ReadUInt64BigEndian(padded.Slice(8));
    return true;
  }
#endif

  public BigInteger ReadInteger(ReadOnlySpan<byte> payload, in ElementInfo element)
  {
    var tag = element.Tag;
    BigInteger magnitude;
    if (tag == WideNegativeInteger || tag == WidePositiveInteger)
    {
      Span<byte> buffer = stackalloc byte[MaximumWideWidth];
      magnitude = new BigInteger(WideMagnitude(tag, payload, element.Mask, buffer), isUnsigned: true, isBigEndian: true);
    }
    else
      magnitude = NarrowMagnitude(tag, payload, element.Mask);
    return tag < IntegerZero ? -magnitude : magnitude;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public double ReadFloating(ReadOnlySpan<byte> payload, in ElementInfo element)
      => BitConverter.Int64BitsToDouble((long)FloatingBits(Read64(payload, element.Mask)));

  /// <summary>A key stores every floating value as a double.</summary>
  public bool TryReadHalf(ReadOnlySpan<byte> payload, in ElementInfo element, out Half value)
  {
    value = default;
    return false;
  }

  /// <summary>A key stores every floating value as a double.</summary>
  public bool TryReadSingle(ReadOnlySpan<byte> payload, in ElementInfo element, out float value)
  {
    value = 0;
    return false;
  }

  public decimal ReadDecimal(ReadOnlySpan<byte> payload, in ElementInfo element)
  {
    var tag = element.Tag;
    if (tag == DecimalZero)
      return 0m;
    _ = TryReadDecimal(payload, DecimalMask(tag, element.Mask), tag == NegativeDecimal, out var number, out _);
    return number;
  }

  public bool ReadBoolean(ReadOnlySpan<byte> payload, in ElementInfo element) => element.Tag == True;

  public DateTime ReadDateTime(ReadOnlySpan<byte> payload, in ElementInfo element)
      => new((long)Read64(payload, element.Mask));

  public DateTimeOffset ReadDateTimeOffset(ReadOnlySpan<byte> payload, in ElementInfo element)
      => new((long)Read64(payload, element.Mask), TimeSpan.Zero);

  public DateOnly ReadDateOnly(ReadOnlySpan<byte> payload, in ElementInfo element)
      => DateOnly.FromDayNumber((int)Read32(payload, element.Mask));

  public TimeOnly ReadTimeOnly(ReadOnlySpan<byte> payload, in ElementInfo element)
      => new((long)Read64(payload, element.Mask));

  public TimeSpan ReadTimeSpan(ReadOnlySpan<byte> payload, in ElementInfo element)
      => new((long)(Read64(payload, element.Mask) ^ SignBit));

  public Guid ReadGuid(ReadOnlySpan<byte> payload, in ElementInfo element)
      => CompositeKeyFormat.ReadGuid(payload, element.Mask);

  public string ReadString(ReadOnlySpan<byte> payload, in ElementInfo element)
      => CompositeKeyFormat.ReadString(Escaped(payload), element.Mask);

  public byte[] ReadBytes(ReadOnlySpan<byte> payload, in ElementInfo element)
      => CompositeKeyFormat.ReadBytes(Escaped(payload), element.Mask);

  /// <summary>The escaped bytes of a byte or string element's payload, without its terminator.</summary>
  static ReadOnlySpan<byte> Escaped(ReadOnlySpan<byte> payload) => payload.Slice(0, payload.Length - 2);

  /// <summary>The magnitude of an integer of at most 8 bytes.</summary>
  static ulong NarrowMagnitude(byte tag, ReadOnlySpan<byte> payload, byte mask)
  {
    if (tag == IntegerZero)
      return 0;
    var width = IntegerWidth(tag, payload, mask, out _);
    return tag < IntegerZero ? Magnitude(payload, width, mask) : ReadBigEndian(payload, width, mask);
  }
}
