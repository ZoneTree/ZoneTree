namespace ZoneTree.Comparers;

/// <summary>
/// Compares read-only byte memory in descending lexicographic order, with a prefix after every longer sequence
/// that begins with it.
/// </summary>
public sealed class ReadOnlyByteMemoryLexicographicComparerDescending : IRefComparer<ReadOnlyMemory<byte>>
{
  public int Compare(in ReadOnlyMemory<byte> x, in ReadOnlyMemory<byte> y)
    => y.Span.SequenceCompareTo(x.Span);
}
