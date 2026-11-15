namespace ZoneTree.Comparers;

/// <summary>Compares native-sized signed integers in descending order.</summary>
public sealed class IntPtrComparerDescending : IRefComparer<nint>
{
  public int Compare(in nint x, in nint y)
  {
    return y.CompareTo(x);
  }
}
