namespace ZoneTree.Comparers;

/// <summary>
/// Compares byte sequences in ascending lexicographic order, with a prefix
/// before every longer sequence that begins with it.
/// </summary>
public sealed class ByteArrayLexicographicComparerAscending : IRefComparer<Memory<byte>>
{
  public int Compare(in Memory<byte> x, in Memory<byte> y)
    => x.Span.SequenceCompareTo(y.Span);
}
