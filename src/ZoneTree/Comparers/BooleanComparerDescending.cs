namespace ZoneTree.Comparers;

/// <summary>Compares booleans in descending order, true before false.</summary>
public sealed class BooleanComparerDescending : IRefComparer<bool>
{
  public int Compare(in bool x, in bool y)
  {
    return y.CompareTo(x);
  }
}
