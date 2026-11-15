namespace ZoneTree.Comparers;

/// <summary>Compares times of day in descending order.</summary>
public sealed class TimeOnlyComparerDescending : IRefComparer<TimeOnly>
{
  public int Compare(in TimeOnly x, in TimeOnly y)
  {
    return y.CompareTo(x);
  }
}
