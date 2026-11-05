namespace ZoneTree.Comparers;

/// <summary>
/// Compares byte arrays in descending lexicographic order, reversing
/// <see cref="BytesLexicographicComparerAscending"/> including prefix ordering.
/// </summary>
public sealed class BytesLexicographicComparerDescending : IRefComparer<byte[]>
{
  public int Compare(in byte[] x, in byte[] y)
    => y.AsSpan().SequenceCompareTo(x.AsSpan());
}
