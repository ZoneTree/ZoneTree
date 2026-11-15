using System.Numerics;
using System.Runtime.CompilerServices;

namespace ZoneTree.Composite;

/// <summary>
/// The writing side of a composite format: one method for each canonical element, which the format writes in its
/// own bytes. <see cref="CompositeElementWriting.Write"/> turns a .NET value into these calls.
/// </summary>
internal interface IElementSink
{
  void WriteNull();
  void WriteBoolean(bool value);
  void WriteSigned(long value);
  void WriteUnsigned(ulong value);
#if NET7_0_OR_GREATER
  void WriteSigned(Int128 value);
  void WriteUnsigned(UInt128 value);
#endif
  void WriteInteger(BigInteger value);
  void WriteFloating(Half value);
  void WriteFloating(float value);
  void WriteFloating(double value);
  void WriteDecimal(decimal value);
  void WriteDateTime(DateTime value);
  void WriteDateTimeOffset(DateTimeOffset value);
  void WriteDateOnly(DateOnly value);
  void WriteTimeOnly(TimeOnly value);
  void WriteTimeSpan(TimeSpan value);
  void WriteGuid(Guid value);
  void WriteString(string value);
  void WriteBytes(ReadOnlySpan<byte> value);
}

/// <summary>Turns a .NET value into the canonical element a format writes, the same for every format.</summary>
internal static class CompositeElementWriting
{
  /// <summary>
  /// Writes a value as its element: every integer type, char and enum as an integer, every floating type as a
  /// floating value, and a null or a nullable without a value as null. A descending element is written as its
  /// value; the format complements it, or refuses it.
  /// </summary>
  /// <exception cref="NotSupportedException">Composite elements do not support the type.</exception>
  internal static void Write<T, TSink>(ref T source, ref TSink sink) where TSink : struct, IElementSink
  {
    // The shape is read here, as the reading side reads it, so that in each value type's own code the JIT folds
    // it to a constant and keeps only the case of the type.
    var shape = ShapeOf<T>.Value;
    // A nullable value's first byte tells whether it has a value, whatever type it holds.
    if (shape.IsNullable && !Unsafe.As<T, bool>(ref source))
    {
      sink.WriteNull();
      return;
    }
    switch (shape.Kind)
    {
      case ElementKind.String:
        if (Unsafe.As<T, string>(ref source) is { } text)
          sink.WriteString(text);
        else
          sink.WriteNull();
        return;
      case ElementKind.ByteArray:
        if (Unsafe.As<T, byte[]>(ref source) is { } array)
          sink.WriteBytes(array);
        else
          sink.WriteNull();
        return;
      case ElementKind.ByteMemory:
        sink.WriteBytes(Value<T, Memory<byte>>(ref source, shape).Span);
        return;
      case ElementKind.ReadOnlyByteMemory:
        sink.WriteBytes(Value<T, ReadOnlyMemory<byte>>(ref source, shape).Span);
        return;
      case ElementKind.Boolean:
        sink.WriteBoolean(Value<T, bool>(ref source, shape));
        return;
      case ElementKind.SByte:
        sink.WriteSigned(Value<T, sbyte>(ref source, shape));
        return;
      case ElementKind.Byte:
        sink.WriteUnsigned(Value<T, byte>(ref source, shape));
        return;
      case ElementKind.Int16:
        sink.WriteSigned(Value<T, short>(ref source, shape));
        return;
      case ElementKind.UInt16:
        sink.WriteUnsigned(Value<T, ushort>(ref source, shape));
        return;
      case ElementKind.Int32:
        sink.WriteSigned(Value<T, int>(ref source, shape));
        return;
      case ElementKind.UInt32:
        sink.WriteUnsigned(Value<T, uint>(ref source, shape));
        return;
      case ElementKind.Int64:
        sink.WriteSigned(Value<T, long>(ref source, shape));
        return;
      case ElementKind.UInt64:
        sink.WriteUnsigned(Value<T, ulong>(ref source, shape));
        return;
      case ElementKind.IntPtr:
        sink.WriteSigned(Value<T, nint>(ref source, shape));
        return;
      case ElementKind.UIntPtr:
        sink.WriteUnsigned(Value<T, nuint>(ref source, shape));
        return;
      case ElementKind.Char:
        sink.WriteUnsigned(Value<T, char>(ref source, shape));
        return;
#if NET7_0_OR_GREATER
      case ElementKind.Int128:
        sink.WriteSigned(Value<T, Int128>(ref source, shape));
        return;
      case ElementKind.UInt128:
        sink.WriteUnsigned(Value<T, UInt128>(ref source, shape));
        return;
#endif
      case ElementKind.BigInteger:
        sink.WriteInteger(Value<T, BigInteger>(ref source, shape));
        return;
      case ElementKind.Half:
        sink.WriteFloating(Value<T, Half>(ref source, shape));
        return;
      case ElementKind.Single:
        sink.WriteFloating(Value<T, float>(ref source, shape));
        return;
      case ElementKind.Double:
        sink.WriteFloating(Value<T, double>(ref source, shape));
        return;
      case ElementKind.Decimal:
        sink.WriteDecimal(Value<T, decimal>(ref source, shape));
        return;
      case ElementKind.DateTime:
        sink.WriteDateTime(Value<T, DateTime>(ref source, shape));
        return;
      case ElementKind.DateTimeOffset:
        sink.WriteDateTimeOffset(Value<T, DateTimeOffset>(ref source, shape));
        return;
      case ElementKind.DateOnly:
        sink.WriteDateOnly(Value<T, DateOnly>(ref source, shape));
        return;
      case ElementKind.TimeOnly:
        sink.WriteTimeOnly(Value<T, TimeOnly>(ref source, shape));
        return;
      case ElementKind.TimeSpan:
        sink.WriteTimeSpan(Value<T, TimeSpan>(ref source, shape));
        return;
      case ElementKind.Guid:
        sink.WriteGuid(Value<T, Guid>(ref source, shape));
        return;
      default:
        throw new NotSupportedException($"A composite element cannot be a {typeof(T)}.");
    }
  }

  /// <summary>The value of a value type, or of its nullable type, which has a value.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static TValue Value<T, TValue>(ref T source, ElementShape shape) where TValue : struct
      => shape.IsNullable ? Unsafe.As<T, TValue?>(ref source).GetValueOrDefault() : Unsafe.As<T, TValue>(ref source);
}
