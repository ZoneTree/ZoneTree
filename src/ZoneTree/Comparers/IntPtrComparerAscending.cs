namespace ZoneTree.Comparers;

/// <summary>Compares native-sized signed integers in ascending order.</summary>
public sealed class IntPtrComparerAscending : IRefComparer<nint>
{
  public int Compare(in nint x, in nint y)
  {
    return x.CompareTo(y);
  }
}
