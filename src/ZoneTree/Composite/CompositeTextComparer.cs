namespace ZoneTree.Composite;

/// <summary>
/// Orders strings by their Unicode code points, the order of their UTF-8 bytes in keys, with a null first, as a null
/// element sorts before every string. Ordinal comparisons order UTF-16 code units, which puts a character above
/// U+FFFF, held as a surrogate pair, before one from U+E000 to U+FFFF; this comparer puts it after, as keys do. Two
/// strings are equal when their chars are.
/// </summary>
internal sealed class CompositeTextComparerAscending : StringComparer
{
  internal static readonly CompositeTextComparerAscending Instance = new();

  CompositeTextComparerAscending()
  {
  }

  public override int Compare(string x, string y) => CodePointOrder.Compare(x, y);

  public override bool Equals(string x, string y) => string.Equals(x, y, StringComparison.Ordinal);

  /// <exception cref="ArgumentNullException">The string is null.</exception>
  public override int GetHashCode(string obj)
  {
    ArgumentNullException.ThrowIfNull(obj);
    return obj.GetHashCode(StringComparison.Ordinal);
  }
}

/// <summary>
/// Orders strings in the reverse of <see cref="CompositeTextComparerAscending"/>, with a null last, as a descending
/// null element sorts after every descending string. Two strings are equal when their chars are.
/// </summary>
internal sealed class CompositeTextComparerDescending : StringComparer
{
  internal static readonly CompositeTextComparerDescending Instance = new();

  CompositeTextComparerDescending()
  {
  }

  public override int Compare(string x, string y) => CodePointOrder.Compare(y, x);

  public override bool Equals(string x, string y) => string.Equals(x, y, StringComparison.Ordinal);

  /// <exception cref="ArgumentNullException">The string is null.</exception>
  public override int GetHashCode(string obj)
  {
    ArgumentNullException.ThrowIfNull(obj);
    return obj.GetHashCode(StringComparison.Ordinal);
  }
}

/// <summary>The code point order of strings, the one order both text comparers take, each in its direction.</summary>
internal static class CodePointOrder
{
  /// <summary>Compares two strings by their code points, a null before every string.</summary>
  internal static int Compare(string x, string y)
  {
    if (ReferenceEquals(x, y))
      return 0;
    if (x == null)
      return -1;
    if (y == null)
      return 1;
    var length = Math.Min(x.Length, y.Length);
#if NET7_0_OR_GREATER
    var at = x.AsSpan(0, length).CommonPrefixLength(y.AsSpan(0, length));
#else
    var at = 0;
    while (at < length && x[at] == y[at])
      ++at;
#endif
    if (at == length)
      return x.Length.CompareTo(y.Length);
    return Order(x[at]).CompareTo(Order(y[at]));
  }

  /// <summary>
  /// Where a code unit sorts among the others in code point order, at the first one two strings differ in: code units
  /// below the surrogates stay, those from U+E000 up go below the surrogates, and the surrogates, which hold the code
  /// points above U+FFFF, go above them all.
  /// </summary>
  static int Order(char unit) => unit < 0xD800 ? unit : unit >= 0xE000 ? unit - 0x800 : unit + 0x2000;
}
