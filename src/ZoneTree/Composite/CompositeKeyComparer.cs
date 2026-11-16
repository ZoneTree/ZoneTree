namespace ZoneTree.Composite;

/// <summary>
/// Compares composite keys in the order of <see cref="Comparers.BytesLexicographicComparerAscending"/>, the default
/// comparer of byte-array keys, for sets, dictionaries and sorts of keys in memory. A key is a byte array, and
/// <c>==</c> on arrays compares references, not bytes.
/// </summary>
/// <remarks>
/// Its one instance is <see cref="CompositeKey.ComparerAscending"/>. A null array is the empty key, as it is to the
/// tree's <see cref="Comparers.BytesLexicographicComparerAscending"/>: it sorts with the empty key, equals it, and
/// hashes as it does.
/// </remarks>
/// <example>
/// <code>
/// var seen = new HashSet&lt;byte[]&gt;(CompositeKey.ComparerAscending);
/// var sorted = keys.Order(CompositeKey.ComparerAscending);
/// </code>
/// </example>
public sealed class CompositeKeyComparerAscending : IComparer<byte[]>, IEqualityComparer<byte[]>
{
  // The order is written out here rather than taken from BytesLexicographicComparerAscending, as it runs on every
  // comparison. It must stay that comparer's order exactly, null as the empty key through AsSpan included, or a set,
  // a dictionary or a sort in memory disagrees with what the tree holds and a scan returns: the unit tests compare the
  // two on every pair of a set of keys and bytes.

  internal static readonly CompositeKeyComparerAscending Instance = new();

  CompositeKeyComparerAscending()
  {
  }

  /// <summary>Compares two keys in ascending key order: byte by byte, a key before every key it starts.</summary>
  public int Compare(byte[] x, byte[] y) => x.AsSpan().SequenceCompareTo(y);

  /// <summary>Whether two keys have the same bytes.</summary>
  public bool Equals(byte[] x, byte[] y) => CompositeKeyEquality.Equals(x, y);

  /// <summary>A hash of a key's bytes.</summary>
  public int GetHashCode(byte[] obj) => CompositeKeyEquality.GetHashCode(obj);
}

/// <summary>
/// Compares composite keys by their bytes in descending order, as a tree created with
/// <see cref="Comparers.BytesLexicographicComparerDescending"/> orders them, for the sets, dictionaries and sorts of
/// keys in memory. Two keys are equal, and hash alike, in either order.
/// </summary>
/// <remarks>
/// Its one instance is <see cref="CompositeKey.ComparerDescending"/>. A null array is the empty key, as it is to the
/// tree's comparer.
/// </remarks>
public sealed class CompositeKeyComparerDescending : IComparer<byte[]>, IEqualityComparer<byte[]>
{
  // The order is written out here rather than taken from BytesLexicographicComparerDescending, as it runs on every
  // comparison. It must stay that comparer's order exactly, null as the empty key through AsSpan included: the unit
  // tests compare the two on every pair of a set of keys and bytes.

  internal static readonly CompositeKeyComparerDescending Instance = new();

  CompositeKeyComparerDescending()
  {
  }

  /// <summary>Compares two keys in descending key order: byte by byte reversed, a key after every key it starts.</summary>
  public int Compare(byte[] x, byte[] y) => y.AsSpan().SequenceCompareTo(x);

  /// <summary>Whether two keys have the same bytes.</summary>
  public bool Equals(byte[] x, byte[] y) => CompositeKeyEquality.Equals(x, y);

  /// <summary>A hash of a key's bytes.</summary>
  public int GetHashCode(byte[] obj) => CompositeKeyEquality.GetHashCode(obj);
}

/// <summary>Which keys are one key, the same in either order: those with the same bytes, a null array the empty key.</summary>
internal static class CompositeKeyEquality
{
  internal static bool Equals(byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y);

  internal static int GetHashCode(byte[] key)
  {
    var hash = new HashCode();
    hash.AddBytes(key);
    return hash.ToHashCode();
  }
}
