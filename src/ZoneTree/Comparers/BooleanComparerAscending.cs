namespace ZoneTree.Comparers;

/// <summary>Compares booleans in ascending order, false before true.</summary>
public sealed class BooleanComparerAscending : IRefComparer<bool>
{
  public int Compare(in bool x, in bool y)
  {
    return x.CompareTo(y);
  }
}
