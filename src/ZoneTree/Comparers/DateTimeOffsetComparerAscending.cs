namespace ZoneTree.Comparers;

/// <summary>Compares points in time in ascending order of their UTC instants, whatever their offsets.</summary>
public sealed class DateTimeOffsetComparerAscending : IRefComparer<DateTimeOffset>
{
  public int Compare(in DateTimeOffset x, in DateTimeOffset y)
  {
    return x.CompareTo(y);
  }
}
