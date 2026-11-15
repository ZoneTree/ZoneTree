namespace ZoneTree.Comparers;

/// <summary>Compares time intervals in descending order.</summary>
public sealed class TimeSpanComparerDescending : IRefComparer<TimeSpan>
{
  public int Compare(in TimeSpan x, in TimeSpan y)
  {
    return y.CompareTo(x);
  }
}
