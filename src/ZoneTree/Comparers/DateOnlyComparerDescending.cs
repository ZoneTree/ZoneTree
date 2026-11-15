namespace ZoneTree.Comparers;

/// <summary>Compares dates in descending order.</summary>
public sealed class DateOnlyComparerDescending : IRefComparer<DateOnly>
{
  public int Compare(in DateOnly x, in DateOnly y)
  {
    return y.CompareTo(x);
  }
}
