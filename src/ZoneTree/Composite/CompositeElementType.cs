namespace ZoneTree.Composite;

/// <summary>
/// The type of an element of a composite key or a composite value. Key elements of different types sort in the
/// order listed here, ascending elements before descending ones. A composite value stores these numbers, so they
/// never change.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720",
    Justification = "The members name the types of the elements, as System.TypeCode does.")]
public enum CompositeElementType
{
  /// <summary>A null string, byte array or nullable value; in a key it sorts before every value.</summary>
  Null = 0,

  /// <summary>Bytes, from a byte array or byte memory; a key orders them lexicographically.</summary>
  Bytes = 1,

  /// <summary>A string; a key orders strings by their Unicode code points.</summary>
  String = 2,

  /// <summary>
  /// An integer of any .NET integer type, a BigInteger, a char or an enum, compared by value whatever type wrote it.
  /// </summary>
  Integer = 3,

  /// <summary>
  /// A Half, a float or a double. A key compares them by value, with one zero and one NaN, which sorts last; a
  /// value keeps each in its own size.
  /// </summary>
  Floating = 4,

  /// <summary>A decimal. A key compares decimals by value, so 1.0 and 1.00 are one key; a value keeps the scale.</summary>
  Decimal = 5,

  /// <summary>A bool; in a key, false before true.</summary>
  Boolean = 6,

  /// <summary>A DateTime. A key keeps only its ticks; a value keeps its kind too.</summary>
  DateTime = 7,

  /// <summary>A DateTimeOffset. A key keeps only its UTC instant; a value keeps its offset too.</summary>
  DateTimeOffset = 8,

  /// <summary>A DateOnly; a key orders dates by day.</summary>
  DateOnly = 9,

  /// <summary>A TimeOnly; a key orders times by time of day.</summary>
  TimeOnly = 10,

  /// <summary>A TimeSpan; a key orders intervals by their signed length.</summary>
  TimeSpan = 11,

  /// <summary>A Guid; a key orders Guids as their text forms sort.</summary>
  Guid = 12
}
