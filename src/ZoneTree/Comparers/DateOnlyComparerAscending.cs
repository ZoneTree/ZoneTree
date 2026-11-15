namespace ZoneTree.Comparers;

/// <summary>Compares dates in ascending order.</summary>
public sealed class DateOnlyComparerAscending : IRefComparer<DateOnly>
{
  public int Compare(in DateOnly x, in DateOnly y)
  {
    return x.CompareTo(y);
  }
}
