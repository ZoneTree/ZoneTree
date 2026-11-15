namespace ZoneTree.Comparers;

/// <summary>
/// Compares read-only byte memory in ascending lexicographic order, with a prefix before every longer sequence
/// that begins with it.
/// </summary>
public sealed class ReadOnlyByteMemoryLexicographicComparerAscending : IRefComparer<ReadOnlyMemory<byte>>
{
  public int Compare(in ReadOnlyMemory<byte> x, in ReadOnlyMemory<byte> y)
    => x.Span.SequenceCompareTo(y.Span);
}
