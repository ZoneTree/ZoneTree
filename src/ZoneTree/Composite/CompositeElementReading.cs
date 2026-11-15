using System.Numerics;
using System.Runtime.CompilerServices;

namespace ZoneTree.Composite;

/// <summary>An element a format found: its type, the tag and mask its payload is read with, and its index.</summary>
internal readonly struct ElementInfo
{
  internal ElementInfo(CompositeElementType type, byte tag, byte mask, int index)
  {
    Type = type;
    Tag = tag;
    Mask = mask;
    Index = index;
  }

  internal CompositeElementType Type { get; }

  /// <summary>The format's own tag of the element, which a key's integers read their width from.</summary>
  internal byte Tag { get; }

  /// <summary>The mask a key's descending element is read through; zero otherwise.</summary>
  internal byte Mask { get; }

  internal int Index { get; }
}

/// <summary>
/// The reading side of a composite format: the value of an element of a type, decoded from its payload as the
/// format stores it. <see cref="CompositeElementReading.Read"/> calls only the method of the type it reads.
/// </summary>
internal interface IElementFormat
{
  /// <summary>An integer's sign and magnitude, unless its magnitude is wider than 64 bits.</summary>
  bool TryReadNarrow(ReadOnlySpan<byte> payload, in ElementInfo element, out bool negative, out ulong magnitude);

#if NET7_0_OR_GREATER
  /// <summary>An integer's sign and magnitude, unless its magnitude is wider than 128 bits.</summary>
  bool TryReadWide(ReadOnlySpan<byte> payload, in ElementInfo element, out bool negative, out UInt128 magnitude);
#endif

  BigInteger ReadInteger(ReadOnlySpan<byte> payload, in ElementInfo element);

  double ReadFloating(ReadOnlySpan<byte> payload, in ElementInfo element);

  /// <summary>A floating value stored as a <see cref="Half"/>, if the format stores it so.</summary>
  bool TryReadHalf(ReadOnlySpan<byte> payload, in ElementInfo element, out Half value);

  /// <summary>A floating value stored as a <see cref="float"/>, if the format stores it so.</summary>
  bool TryReadSingle(ReadOnlySpan<byte> payload, in ElementInfo element, out float value);

  decimal ReadDecimal(ReadOnlySpan<byte> payload, in ElementInfo element);
  bool ReadBoolean(ReadOnlySpan<byte> payload, in ElementInfo element);
  DateTime ReadDateTime(ReadOnlySpan<byte> payload, in ElementInfo element);
  DateTimeOffset ReadDateTimeOffset(ReadOnlySpan<byte> payload, in ElementInfo element);
  DateOnly ReadDateOnly(ReadOnlySpan<byte> payload, in ElementInfo element);
  TimeOnly ReadTimeOnly(ReadOnlySpan<byte> payload, in ElementInfo element);
  TimeSpan ReadTimeSpan(ReadOnlySpan<byte> payload, in ElementInfo element);
  Guid ReadGuid(ReadOnlySpan<byte> payload, in ElementInfo element);

  /// <exception cref="ArgumentException">The string's bytes are not valid UTF-8.</exception>
  string ReadString(ReadOnlySpan<byte> payload, in ElementInfo element);

  byte[] ReadBytes(ReadOnlySpan<byte> payload, in ElementInfo element);
}

/// <summary>
/// The reading rules every composite format shares: which elements read as which .NET types, the ranges and the
/// exactness a type needs, and the errors when it has neither.
/// </summary>
internal static class CompositeElementReading
{
  /// <summary>
  /// Reads an element as a value of a type. An integer reads as any integer type, char or enum that holds it, a
  /// floating value as any floating type that holds it exactly, and a null as null for strings, byte arrays and
  /// nullable value types; every other element reads only as its own type.
  /// </summary>
  /// <exception cref="InvalidCastException">The element has another type.</exception>
  /// <exception cref="OverflowException">The type cannot hold the element's value exactly.</exception>
  /// <exception cref="ArgumentException">A string element's bytes are not valid UTF-8.</exception>
  /// <exception cref="NotSupportedException">Composite elements do not support the type.</exception>
  internal static T Read<T, TFormat>(ReadOnlySpan<byte> payload, in ElementInfo element) where TFormat : struct, IElementFormat
  {
    var shape = ShapeOf<T>.Value;
    if (shape.Kind == ElementKind.Unsupported)
      throw new NotSupportedException($"A composite element cannot be read as a {typeof(T)}.");
    if (shape.IsDescending)
      throw new NotSupportedException("An element reads back as a value of its own type, not as a Descending<T>.");
    if (element.Type == CompositeElementType.Null)
    {
      if (shape.IsNullable || shape.Kind is ElementKind.String or ElementKind.ByteArray)
        return default;
      throw Mismatch<T>(element);
    }
    var format = default(TFormat);
    switch (shape.Kind)
    {
      case ElementKind.String:
        Expect<T>(element, CompositeElementType.String);
        var text = format.ReadString(payload, element);
        return Unsafe.As<string, T>(ref text);
      case ElementKind.ByteArray:
        Expect<T>(element, CompositeElementType.Bytes);
        var array = format.ReadBytes(payload, element);
        return Unsafe.As<byte[], T>(ref array);
      case ElementKind.ByteMemory:
        Expect<T>(element, CompositeElementType.Bytes);
        return Give<T, Memory<byte>>(shape, format.ReadBytes(payload, element));
      case ElementKind.ReadOnlyByteMemory:
        Expect<T>(element, CompositeElementType.Bytes);
        return Give<T, ReadOnlyMemory<byte>>(shape, format.ReadBytes(payload, element));
      case ElementKind.Boolean:
        Expect<T>(element, CompositeElementType.Boolean);
        return Give<T, bool>(shape, format.ReadBoolean(payload, element));
      case ElementKind.SByte:
        return Give<T, sbyte>(shape, (sbyte)Signed<T, TFormat>(payload, element, sbyte.MinValue, sbyte.MaxValue));
      case ElementKind.Byte:
        return Give<T, byte>(shape, (byte)Unsigned<T, TFormat>(payload, element, byte.MaxValue));
      case ElementKind.Int16:
        return Give<T, short>(shape, (short)Signed<T, TFormat>(payload, element, short.MinValue, short.MaxValue));
      case ElementKind.UInt16:
        return Give<T, ushort>(shape, (ushort)Unsigned<T, TFormat>(payload, element, ushort.MaxValue));
      case ElementKind.Int32:
        return Give<T, int>(shape, (int)Signed<T, TFormat>(payload, element, int.MinValue, int.MaxValue));
      case ElementKind.UInt32:
        return Give<T, uint>(shape, (uint)Unsigned<T, TFormat>(payload, element, uint.MaxValue));
      case ElementKind.Int64:
        return Give<T, long>(shape, Signed<T, TFormat>(payload, element, long.MinValue, long.MaxValue));
      case ElementKind.UInt64:
        return Give<T, ulong>(shape, Unsigned<T, TFormat>(payload, element, ulong.MaxValue));
      case ElementKind.IntPtr:
        return Give<T, nint>(shape, (nint)Signed<T, TFormat>(payload, element, nint.MinValue, nint.MaxValue));
      case ElementKind.UIntPtr:
        return Give<T, nuint>(shape, (nuint)Unsigned<T, TFormat>(payload, element, nuint.MaxValue));
      case ElementKind.Char:
        return Give<T, char>(shape, (char)Unsigned<T, TFormat>(payload, element, char.MaxValue));
#if NET7_0_OR_GREATER
      case ElementKind.Int128:
        Expect<T>(element, CompositeElementType.Integer);
        if (format.TryReadWide(payload, element, out var negative128, out var magnitude128))
        {
          var limit = (UInt128)Int128.MaxValue;
          if (negative128 && magnitude128 <= limit + 1)
            return Give<T, Int128>(shape, magnitude128 == limit + 1 ? Int128.MinValue : -(Int128)magnitude128);
          if (!negative128 && magnitude128 <= limit)
            return Give<T, Int128>(shape, (Int128)magnitude128);
        }
        throw TooLarge<T>(format.ReadInteger(payload, element), element);
      case ElementKind.UInt128:
        Expect<T>(element, CompositeElementType.Integer);
        if (format.TryReadWide(payload, element, out var negativeUnsigned, out var unsigned128) && !negativeUnsigned)
          return Give<T, UInt128>(shape, unsigned128);
        throw TooLarge<T>(format.ReadInteger(payload, element), element);
#endif
      case ElementKind.BigInteger:
        Expect<T>(element, CompositeElementType.Integer);
        return Give<T, BigInteger>(shape, format.TryReadNarrow(payload, element, out var negativeBig, out var magnitudeBig)
            ? (negativeBig ? -(BigInteger)magnitudeBig : magnitudeBig)
            : format.ReadInteger(payload, element));
      case ElementKind.Half:
        Expect<T>(element, CompositeElementType.Floating);
        if (format.TryReadHalf(payload, element, out var storedHalf))
          return Give<T, Half>(shape, storedHalf);
        var wideHalf = format.ReadFloating(payload, element);
        var half = (Half)wideHalf;
        if ((double)half != wideHalf && !double.IsNaN(wideHalf))
          throw Inexact<T>(wideHalf, element);
        return Give<T, Half>(shape, half);
      case ElementKind.Single:
        Expect<T>(element, CompositeElementType.Floating);
        if (format.TryReadSingle(payload, element, out var storedSingle))
          return Give<T, float>(shape, storedSingle);
        var wideSingle = format.ReadFloating(payload, element);
        var single = (float)wideSingle;
        if (single != wideSingle && !double.IsNaN(wideSingle))
          throw Inexact<T>(wideSingle, element);
        return Give<T, float>(shape, single);
      case ElementKind.Double:
        Expect<T>(element, CompositeElementType.Floating);
        return Give<T, double>(shape, format.ReadFloating(payload, element));
      case ElementKind.Decimal:
        Expect<T>(element, CompositeElementType.Decimal);
        return Give<T, decimal>(shape, format.ReadDecimal(payload, element));
      case ElementKind.DateTime:
        Expect<T>(element, CompositeElementType.DateTime);
        return Give<T, DateTime>(shape, format.ReadDateTime(payload, element));
      case ElementKind.DateTimeOffset:
        Expect<T>(element, CompositeElementType.DateTimeOffset);
        return Give<T, DateTimeOffset>(shape, format.ReadDateTimeOffset(payload, element));
      case ElementKind.DateOnly:
        Expect<T>(element, CompositeElementType.DateOnly);
        return Give<T, DateOnly>(shape, format.ReadDateOnly(payload, element));
      case ElementKind.TimeOnly:
        Expect<T>(element, CompositeElementType.TimeOnly);
        return Give<T, TimeOnly>(shape, format.ReadTimeOnly(payload, element));
      case ElementKind.TimeSpan:
        Expect<T>(element, CompositeElementType.TimeSpan);
        return Give<T, TimeSpan>(shape, format.ReadTimeSpan(payload, element));
      case ElementKind.Guid:
        Expect<T>(element, CompositeElementType.Guid);
        return Give<T, Guid>(shape, format.ReadGuid(payload, element));
      default:
        throw new NotSupportedException($"A composite element cannot be read as a {typeof(T)}.");
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static T Give<T, TValue>(ElementShape shape, TValue value) where TValue : struct
  {
    if (shape.IsNullable)
    {
      TValue? nullable = value;
      return Unsafe.As<TValue?, T>(ref nullable);
    }
    return Unsafe.As<TValue, T>(ref value);
  }

  static long Signed<T, TFormat>(ReadOnlySpan<byte> payload, in ElementInfo element, long minimum, long maximum)
      where TFormat : struct, IElementFormat
  {
    Expect<T>(element, CompositeElementType.Integer);
    if (default(TFormat).TryReadNarrow(payload, element, out var negative, out var magnitude))
    {
      if (negative)
      {
        if (magnitude <= (ulong)-(minimum + 1) + 1)
          return magnitude == CompositeKeyFormat.SignBit ? long.MinValue : -(long)magnitude;
      }
      else if (magnitude <= (ulong)maximum)
        return (long)magnitude;
    }
    throw TooLarge<T>(default(TFormat).ReadInteger(payload, element), element);
  }

  static ulong Unsigned<T, TFormat>(ReadOnlySpan<byte> payload, in ElementInfo element, ulong maximum)
      where TFormat : struct, IElementFormat
  {
    Expect<T>(element, CompositeElementType.Integer);
    if (default(TFormat).TryReadNarrow(payload, element, out var negative, out var magnitude) && !negative && magnitude <= maximum)
      return magnitude;
    throw TooLarge<T>(default(TFormat).ReadInteger(payload, element), element);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static void Expect<T>(in ElementInfo element, CompositeElementType expected)
  {
    if (element.Type != expected)
      throw Mismatch<T>(element);
  }

  static InvalidCastException Mismatch<T>(in ElementInfo element)
      => new($"Element {element.Index} is {element.Type}, which cannot be read as a {typeof(T)}.");

  static OverflowException TooLarge<T>(BigInteger value, in ElementInfo element)
      => new($"Element {element.Index} holds {value}, which a {typeof(T)} does not hold.");

  static OverflowException Inexact<T>(double value, in ElementInfo element)
      => new($"Element {element.Index} holds {value}, which a {typeof(T)} does not hold exactly.");
}
