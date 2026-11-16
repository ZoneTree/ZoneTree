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

  /// <summary>A string, unless its bytes are not valid UTF-8.</summary>
  bool TryReadString(ReadOnlySpan<byte> payload, in ElementInfo element, out string value);

  byte[] ReadBytes(ReadOnlySpan<byte> payload, in ElementInfo element);
}

/// <summary>What a read does with an element its type does not hold: throws, or returns false.</summary>
internal interface IReadMode
{
  bool Throws { get; }
}

internal readonly struct ThrowingRead : IReadMode
{
  public bool Throws => true;
}

internal readonly struct TryingRead : IReadMode
{
  public bool Throws => false;
}

/// <summary>
/// The reading rules every composite format shares: which elements read as which .NET types, the ranges and the
/// exactness a type needs, and the errors when it has neither.
/// </summary>
/// <remarks>
/// One set of rules serves both a read that throws and one that returns false: the mode is a struct type
/// argument, so the JIT keeps only the mode's own branches in each instantiation.
/// </remarks>
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
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  internal static T Read<T, TFormat>(ReadOnlySpan<byte> payload, in ElementInfo element) where TFormat : struct, IElementFormat
  {
    Read<T, TFormat, ThrowingRead>(payload, element, out var value);
    return value;
  }

  /// <summary>Reads an element as a value of a type, by the rules of <see cref="Read{T, TFormat}"/>.</summary>
  /// <returns>False where <see cref="Read{T, TFormat}"/> throws for the element.</returns>
  /// <exception cref="NotSupportedException">Composite elements do not support the type.</exception>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  internal static bool TryRead<T, TFormat>(ReadOnlySpan<byte> payload, in ElementInfo element, out T value)
      where TFormat : struct, IElementFormat
      => Read<T, TFormat, TryingRead>(payload, element, out value);

  /// <summary>Checks that a type is one an element reads as: a supported type, and not a <see cref="Descending{T}"/>.</summary>
  /// <exception cref="NotSupportedException">Composite elements do not support the type.</exception>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  internal static void CheckReadable<T>()
  {
    var shape = ShapeOf<T>.Value;
    if (shape.Kind == ElementKind.Unsupported)
      throw new NotSupportedException($"A composite element cannot be read as a {typeof(T)}.");
    if (shape.IsDescending)
      throw new NotSupportedException("An element reads back as a value of its own type, not as a Descending<T>.");
  }

  static bool Read<T, TFormat, TMode>(ReadOnlySpan<byte> payload, in ElementInfo element, out T value)
      where TFormat : struct, IElementFormat
      where TMode : struct, IReadMode
  {
    CheckReadable<T>();
    var shape = ShapeOf<T>.Value;
    value = default;
    if (element.Type == CompositeElementType.Null)
      return shape.IsNullable || shape.Kind is ElementKind.String or ElementKind.ByteArray || Mismatch<T, TMode>(element);
    var format = default(TFormat);
    switch (shape.Kind)
    {
      case ElementKind.String:
        if (element.Type != CompositeElementType.String)
          return Mismatch<T, TMode>(element);
        string text;
        if (default(TMode).Throws)
          text = ReadText(format, payload, element);
        else if (!format.TryReadString(payload, element, out text))
          return false;
        value = Unsafe.As<string, T>(ref text);
        return true;
      case ElementKind.ByteArray:
        if (element.Type != CompositeElementType.Bytes)
          return Mismatch<T, TMode>(element);
        var array = format.ReadBytes(payload, element);
        value = Unsafe.As<byte[], T>(ref array);
        return true;
      case ElementKind.ByteMemory:
        return element.Type == CompositeElementType.Bytes
            ? Give(shape, (Memory<byte>)format.ReadBytes(payload, element), out value)
            : Mismatch<T, TMode>(element);
      case ElementKind.ReadOnlyByteMemory:
        return element.Type == CompositeElementType.Bytes
            ? Give(shape, (ReadOnlyMemory<byte>)format.ReadBytes(payload, element), out value)
            : Mismatch<T, TMode>(element);
      case ElementKind.Boolean:
        return element.Type == CompositeElementType.Boolean
            ? Give(shape, format.ReadBoolean(payload, element), out value)
            : Mismatch<T, TMode>(element);
      case ElementKind.SByte:
        return Signed<T, TFormat, TMode>(payload, element, sbyte.MinValue, sbyte.MaxValue, out var int8) &&
            Give(shape, (sbyte)int8, out value);
      case ElementKind.Byte:
        return Unsigned<T, TFormat, TMode>(payload, element, byte.MaxValue, out var uint8) &&
            Give(shape, (byte)uint8, out value);
      case ElementKind.Int16:
        return Signed<T, TFormat, TMode>(payload, element, short.MinValue, short.MaxValue, out var int16) &&
            Give(shape, (short)int16, out value);
      case ElementKind.UInt16:
        return Unsigned<T, TFormat, TMode>(payload, element, ushort.MaxValue, out var uint16) &&
            Give(shape, (ushort)uint16, out value);
      case ElementKind.Int32:
        return Signed<T, TFormat, TMode>(payload, element, int.MinValue, int.MaxValue, out var int32) &&
            Give(shape, (int)int32, out value);
      case ElementKind.UInt32:
        return Unsigned<T, TFormat, TMode>(payload, element, uint.MaxValue, out var uint32) &&
            Give(shape, (uint)uint32, out value);
      case ElementKind.Int64:
        return Signed<T, TFormat, TMode>(payload, element, long.MinValue, long.MaxValue, out var int64) &&
            Give(shape, int64, out value);
      case ElementKind.UInt64:
        return Unsigned<T, TFormat, TMode>(payload, element, ulong.MaxValue, out var uint64) &&
            Give(shape, uint64, out value);
      case ElementKind.IntPtr:
        return Signed<T, TFormat, TMode>(payload, element, nint.MinValue, nint.MaxValue, out var native) &&
            Give(shape, (nint)native, out value);
      case ElementKind.UIntPtr:
        return Unsigned<T, TFormat, TMode>(payload, element, nuint.MaxValue, out var unsignedNative) &&
            Give(shape, (nuint)unsignedNative, out value);
      case ElementKind.Char:
        return Unsigned<T, TFormat, TMode>(payload, element, char.MaxValue, out var character) &&
            Give(shape, (char)character, out value);
#if NET7_0_OR_GREATER
      case ElementKind.Int128:
        if (element.Type != CompositeElementType.Integer)
          return Mismatch<T, TMode>(element);
        if (format.TryReadWide(payload, element, out var negative128, out var magnitude128))
        {
          var limit = (UInt128)Int128.MaxValue;
          if (negative128 && magnitude128 <= limit + 1)
            return Give(shape, magnitude128 == limit + 1 ? Int128.MinValue : -(Int128)magnitude128, out value);
          if (!negative128 && magnitude128 <= limit)
            return Give(shape, (Int128)magnitude128, out value);
        }
        return TooLarge<T, TFormat, TMode>(payload, element);
      case ElementKind.UInt128:
        if (element.Type != CompositeElementType.Integer)
          return Mismatch<T, TMode>(element);
        if (format.TryReadWide(payload, element, out var negativeUnsigned, out var unsigned128) && !negativeUnsigned)
          return Give(shape, unsigned128, out value);
        return TooLarge<T, TFormat, TMode>(payload, element);
#endif
      case ElementKind.BigInteger:
        if (element.Type != CompositeElementType.Integer)
          return Mismatch<T, TMode>(element);
        return Give(shape, format.TryReadNarrow(payload, element, out var negativeBig, out var magnitudeBig)
            ? (negativeBig ? -(BigInteger)magnitudeBig : magnitudeBig)
            : format.ReadInteger(payload, element), out value);
      case ElementKind.Half:
        if (element.Type != CompositeElementType.Floating)
          return Mismatch<T, TMode>(element);
        if (format.TryReadHalf(payload, element, out var storedHalf))
          return Give(shape, storedHalf, out value);
        var wideHalf = format.ReadFloating(payload, element);
        var half = (Half)wideHalf;
        if ((double)half != wideHalf && !double.IsNaN(wideHalf))
          return Inexact<T, TMode>(wideHalf, element);
        return Give(shape, half, out value);
      case ElementKind.Single:
        if (element.Type != CompositeElementType.Floating)
          return Mismatch<T, TMode>(element);
        if (format.TryReadSingle(payload, element, out var storedSingle))
          return Give(shape, storedSingle, out value);
        var wideSingle = format.ReadFloating(payload, element);
        var single = (float)wideSingle;
        if (single != wideSingle && !double.IsNaN(wideSingle))
          return Inexact<T, TMode>(wideSingle, element);
        return Give(shape, single, out value);
      case ElementKind.Double:
        return element.Type == CompositeElementType.Floating
            ? Give(shape, format.ReadFloating(payload, element), out value)
            : Mismatch<T, TMode>(element);
      case ElementKind.Decimal:
        return element.Type == CompositeElementType.Decimal
            ? Give(shape, format.ReadDecimal(payload, element), out value)
            : Mismatch<T, TMode>(element);
      case ElementKind.DateTime:
        return element.Type == CompositeElementType.DateTime
            ? Give(shape, format.ReadDateTime(payload, element), out value)
            : Mismatch<T, TMode>(element);
      case ElementKind.DateTimeOffset:
        return element.Type == CompositeElementType.DateTimeOffset
            ? Give(shape, format.ReadDateTimeOffset(payload, element), out value)
            : Mismatch<T, TMode>(element);
      case ElementKind.DateOnly:
        return element.Type == CompositeElementType.DateOnly
            ? Give(shape, format.ReadDateOnly(payload, element), out value)
            : Mismatch<T, TMode>(element);
      case ElementKind.TimeOnly:
        return element.Type == CompositeElementType.TimeOnly
            ? Give(shape, format.ReadTimeOnly(payload, element), out value)
            : Mismatch<T, TMode>(element);
      case ElementKind.TimeSpan:
        return element.Type == CompositeElementType.TimeSpan
            ? Give(shape, format.ReadTimeSpan(payload, element), out value)
            : Mismatch<T, TMode>(element);
      case ElementKind.Guid:
        return element.Type == CompositeElementType.Guid
            ? Give(shape, format.ReadGuid(payload, element), out value)
            : Mismatch<T, TMode>(element);
      default:
        throw new NotSupportedException($"A composite element cannot be read as a {typeof(T)}.");
    }
  }

  /// <summary>Reads a string element, decoding its UTF-8 bytes once.</summary>
  /// <exception cref="ArgumentException">The element's bytes are not valid UTF-8.</exception>
  static string ReadText<TFormat>(TFormat format, ReadOnlySpan<byte> payload, in ElementInfo element)
      where TFormat : struct, IElementFormat
  {
    try
    {
      return format.ReadString(payload, element);
    }
    catch (System.Text.DecoderFallbackException error)
    {
      throw new ArgumentException($"Element {element.Index} is a string whose bytes are not valid UTF-8.", error);
    }
  }

  /// <summary>Gives a value of a value type, or of its nullable type, as the type read.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static bool Give<T, TValue>(ElementShape shape, TValue result, out T value) where TValue : struct
  {
    if (shape.IsNullable)
    {
      TValue? nullable = result;
      value = Unsafe.As<TValue?, T>(ref nullable);
    }
    else
      value = Unsafe.As<TValue, T>(ref result);
    return true;
  }

  static bool Signed<T, TFormat, TMode>(ReadOnlySpan<byte> payload, in ElementInfo element, long minimum, long maximum, out long result)
      where TFormat : struct, IElementFormat
      where TMode : struct, IReadMode
  {
    result = 0;
    if (element.Type != CompositeElementType.Integer)
      return Mismatch<T, TMode>(element);
    if (default(TFormat).TryReadNarrow(payload, element, out var negative, out var magnitude))
    {
      if (negative)
      {
        if (magnitude <= (ulong)-(minimum + 1) + 1)
        {
          result = magnitude == CompositeKeyFormat.SignBit ? long.MinValue : -(long)magnitude;
          return true;
        }
      }
      else if (magnitude <= (ulong)maximum)
      {
        result = (long)magnitude;
        return true;
      }
    }
    return TooLarge<T, TFormat, TMode>(payload, element);
  }

  static bool Unsigned<T, TFormat, TMode>(ReadOnlySpan<byte> payload, in ElementInfo element, ulong maximum, out ulong result)
      where TFormat : struct, IElementFormat
      where TMode : struct, IReadMode
  {
    result = 0;
    if (element.Type != CompositeElementType.Integer)
      return Mismatch<T, TMode>(element);
    if (default(TFormat).TryReadNarrow(payload, element, out var negative, out var magnitude) && !negative && magnitude <= maximum)
    {
      result = magnitude;
      return true;
    }
    return TooLarge<T, TFormat, TMode>(payload, element);
  }

  // A read that returns false only returns it; one that throws builds its exception, with the element's value
  // for an integer or a floating value the type does not hold.
  static bool Mismatch<T, TMode>(in ElementInfo element) where TMode : struct, IReadMode
      => default(TMode).Throws
          ? throw new InvalidCastException($"Element {element.Index} is {element.Type}, which cannot be read as a {typeof(T)}.")
          : false;

  static bool TooLarge<T, TFormat, TMode>(ReadOnlySpan<byte> payload, in ElementInfo element)
      where TFormat : struct, IElementFormat
      where TMode : struct, IReadMode
      => default(TMode).Throws
          ? throw new OverflowException(
              $"Element {element.Index} holds {default(TFormat).ReadInteger(payload, element)}, which a {typeof(T)} does not hold.")
          : false;

  static bool Inexact<T, TMode>(double value, in ElementInfo element) where TMode : struct, IReadMode
      => default(TMode).Throws
          ? throw new OverflowException($"Element {element.Index} holds {value}, which a {typeof(T)} does not hold exactly.")
          : false;
}
