namespace ZoneTree.Comparers;

/// <summary>
/// Compares byte arrays in ascending lexicographic order, with a prefix before every longer
/// array that begins with it. A null array compares as an empty one.
/// </summary>
public sealed class BytesLexicographicComparerAscending : IRefComparer<byte[]>
{
  public int Compare(in byte[] x, in byte[] y)
    => x.AsSpan().SequenceCompareTo(y.AsSpan());
}
