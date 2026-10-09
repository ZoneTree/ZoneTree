namespace ZoneTree.Comparers;

/// <summary>
/// Compares byte sequences in ascending lexicographic order, with a prefix
/// before every longer sequence that begins with it.
/// </summary>
public sealed class ByteArrayLexicographicComparerAscending : IRefComparer<Memory<byte>>
{
  public int Compare(in Memory<byte> x, in Memory<byte> y)
  {
    var len = Math.Min(x.Length, y.Length);
    var spanX = x.Span;
    var spanY = y.Span;
    for (var i = 0; i < len; ++i)
    {
      var r = spanX[i].CompareTo(spanY[i]);
      if (r < 0)
        return -1;
      if (r > 0)
        return 1;
    }
    return x.Length.CompareTo(y.Length);
  }
}
