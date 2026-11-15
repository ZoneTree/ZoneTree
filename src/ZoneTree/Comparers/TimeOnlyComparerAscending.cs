namespace ZoneTree.Comparers;

/// <summary>Compares times of day in ascending order.</summary>
public sealed class TimeOnlyComparerAscending : IRefComparer<TimeOnly>
{
  public int Compare(in TimeOnly x, in TimeOnly y)
  {
    return x.CompareTo(y);
  }
}
