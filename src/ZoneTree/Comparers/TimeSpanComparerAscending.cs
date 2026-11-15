namespace ZoneTree.Comparers;

/// <summary>Compares time intervals in ascending order.</summary>
public sealed class TimeSpanComparerAscending : IRefComparer<TimeSpan>
{
  public int Compare(in TimeSpan x, in TimeSpan y)
  {
    return x.CompareTo(y);
  }
}
