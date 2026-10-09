namespace ZoneTree.Comparers;

/// <summary>
/// Compares byte sequences in descending lexicographic order, reversing
/// <see cref="ByteArrayLexicographicComparerAscending"/> including prefix ordering.
/// </summary>
public sealed class ByteArrayLexicographicComparerDescending : IRefComparer<Memory<byte>>
{
  public int Compare(in Memory<byte> x, in Memory<byte> y)
    => y.Span.SequenceCompareTo(x.Span);
}
