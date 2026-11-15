using System.Numerics;

namespace ZoneTree.Composite;

/// <summary>The .NET types composite key elements are written from and read as.</summary>
internal enum ElementKind
{
  Unsupported,
  Boolean,
  SByte,
  Byte,
  Int16,
  UInt16,
  Int32,
  UInt32,
  Int64,
  UInt64,
  IntPtr,
  UIntPtr,
  Int128,
  UInt128,
  BigInteger,
  Char,
  Half,
  Single,
  Double,
  Decimal,
  DateTime,
  DateTimeOffset,
  DateOnly,
  TimeOnly,
  TimeSpan,
  Guid,
  String,
  ByteArray,
  ByteMemory,
  ReadOnlyByteMemory
}

/// <summary>
/// How a .NET type is written as an element: its kind, whether it is a nullable value type, and whether it is
/// a <see cref="Descending{T}"/> of the type. An enum type has the kind of its underlying type.
/// </summary>
internal readonly record struct ElementShape(ElementKind Kind, bool IsNullable, bool IsDescending)
{
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1508",
      Justification = "default(T) of a Descending<T> is a struct, not null.")]
  internal static ElementShape Of<T>()
  {
    if (default(T) is IDescendingElement descending)
    {
      var inner = descending.Inner;
      return inner.Kind == ElementKind.Unsupported || inner.IsDescending ? default : inner with { IsDescending = true };
    }
    var kind = KindOf<T>();
    if (kind != ElementKind.Unsupported)
      return new(kind, false, false);
    kind = NullableKindOf<T>();
    if (kind != ElementKind.Unsupported)
      return new(kind, true, false);
    // An enum is written as its underlying integer, which the reader turns back into the enum.
    if (typeof(T).IsEnum)
      return new(UnderlyingKind(Type.GetTypeCode(typeof(T))), false, false);
    if (Nullable.GetUnderlyingType(typeof(T)) is { IsEnum: true } underlying)
      return new(UnderlyingKind(Type.GetTypeCode(underlying)), true, false);
    return default;
  }

  static ElementKind UnderlyingKind(TypeCode code) => code switch
  {
    TypeCode.SByte => ElementKind.SByte,
    TypeCode.Byte => ElementKind.Byte,
    TypeCode.Int16 => ElementKind.Int16,
    TypeCode.UInt16 => ElementKind.UInt16,
    TypeCode.Int32 => ElementKind.Int32,
    TypeCode.UInt32 => ElementKind.UInt32,
    TypeCode.Int64 => ElementKind.Int64,
    TypeCode.UInt64 => ElementKind.UInt64,
    TypeCode.Char => ElementKind.Char,
    _ => ElementKind.Unsupported
  };

  static ElementKind KindOf<T>()
  {
    if (typeof(T) == typeof(string))
      return ElementKind.String;
    if (typeof(T) == typeof(long))
      return ElementKind.Int64;
    if (typeof(T) == typeof(int))
      return ElementKind.Int32;
    if (typeof(T) == typeof(Guid))
      return ElementKind.Guid;
    if (typeof(T) == typeof(DateTime))
      return ElementKind.DateTime;
    if (typeof(T) == typeof(DateTimeOffset))
      return ElementKind.DateTimeOffset;
    if (typeof(T) == typeof(byte[]))
      return ElementKind.ByteArray;
    if (typeof(T) == typeof(Memory<byte>))
      return ElementKind.ByteMemory;
    if (typeof(T) == typeof(ReadOnlyMemory<byte>))
      return ElementKind.ReadOnlyByteMemory;
    if (typeof(T) == typeof(bool))
      return ElementKind.Boolean;
    if (typeof(T) == typeof(ulong))
      return ElementKind.UInt64;
    if (typeof(T) == typeof(uint))
      return ElementKind.UInt32;
    if (typeof(T) == typeof(short))
      return ElementKind.Int16;
    if (typeof(T) == typeof(ushort))
      return ElementKind.UInt16;
    if (typeof(T) == typeof(byte))
      return ElementKind.Byte;
    if (typeof(T) == typeof(sbyte))
      return ElementKind.SByte;
    if (typeof(T) == typeof(nint))
      return ElementKind.IntPtr;
    if (typeof(T) == typeof(nuint))
      return ElementKind.UIntPtr;
#if NET7_0_OR_GREATER
    if (typeof(T) == typeof(Int128))
      return ElementKind.Int128;
    if (typeof(T) == typeof(UInt128))
      return ElementKind.UInt128;
#endif
    if (typeof(T) == typeof(BigInteger))
      return ElementKind.BigInteger;
    if (typeof(T) == typeof(char))
      return ElementKind.Char;
    if (typeof(T) == typeof(double))
      return ElementKind.Double;
    if (typeof(T) == typeof(float))
      return ElementKind.Single;
    if (typeof(T) == typeof(Half))
      return ElementKind.Half;
    if (typeof(T) == typeof(decimal))
      return ElementKind.Decimal;
    if (typeof(T) == typeof(DateOnly))
      return ElementKind.DateOnly;
    if (typeof(T) == typeof(TimeOnly))
      return ElementKind.TimeOnly;
    if (typeof(T) == typeof(TimeSpan))
      return ElementKind.TimeSpan;
    return ElementKind.Unsupported;
  }

  static ElementKind NullableKindOf<T>()
  {
    if (typeof(T) == typeof(long?))
      return ElementKind.Int64;
    if (typeof(T) == typeof(int?))
      return ElementKind.Int32;
    if (typeof(T) == typeof(Guid?))
      return ElementKind.Guid;
    if (typeof(T) == typeof(DateTime?))
      return ElementKind.DateTime;
    if (typeof(T) == typeof(DateTimeOffset?))
      return ElementKind.DateTimeOffset;
    if (typeof(T) == typeof(Memory<byte>?))
      return ElementKind.ByteMemory;
    if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
      return ElementKind.ReadOnlyByteMemory;
    if (typeof(T) == typeof(bool?))
      return ElementKind.Boolean;
    if (typeof(T) == typeof(ulong?))
      return ElementKind.UInt64;
    if (typeof(T) == typeof(uint?))
      return ElementKind.UInt32;
    if (typeof(T) == typeof(short?))
      return ElementKind.Int16;
    if (typeof(T) == typeof(ushort?))
      return ElementKind.UInt16;
    if (typeof(T) == typeof(byte?))
      return ElementKind.Byte;
    if (typeof(T) == typeof(sbyte?))
      return ElementKind.SByte;
    if (typeof(T) == typeof(nint?))
      return ElementKind.IntPtr;
    if (typeof(T) == typeof(nuint?))
      return ElementKind.UIntPtr;
#if NET7_0_OR_GREATER
    if (typeof(T) == typeof(Int128?))
      return ElementKind.Int128;
    if (typeof(T) == typeof(UInt128?))
      return ElementKind.UInt128;
#endif
    if (typeof(T) == typeof(BigInteger?))
      return ElementKind.BigInteger;
    if (typeof(T) == typeof(char?))
      return ElementKind.Char;
    if (typeof(T) == typeof(double?))
      return ElementKind.Double;
    if (typeof(T) == typeof(float?))
      return ElementKind.Single;
    if (typeof(T) == typeof(Half?))
      return ElementKind.Half;
    if (typeof(T) == typeof(decimal?))
      return ElementKind.Decimal;
    if (typeof(T) == typeof(DateOnly?))
      return ElementKind.DateOnly;
    if (typeof(T) == typeof(TimeOnly?))
      return ElementKind.TimeOnly;
    if (typeof(T) == typeof(TimeSpan?))
      return ElementKind.TimeSpan;
    return ElementKind.Unsupported;
  }
}

/// <summary>The shape of a type, worked out once.</summary>
internal static class ShapeOf<T>
{
  internal static readonly ElementShape Value = ElementShape.Of<T>();
}

/// <summary>A <see cref="Descending{T}"/>, which names the shape of the type it wraps.</summary>
internal interface IDescendingElement
{
  ElementShape Inner { get; }
}
