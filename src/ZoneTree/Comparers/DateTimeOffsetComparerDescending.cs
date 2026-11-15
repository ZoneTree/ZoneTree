namespace ZoneTree.Comparers;

/// <summary>Compares points in time in descending order of their UTC instants, whatever their offsets.</summary>
public sealed class DateTimeOffsetComparerDescending : IRefComparer<DateTimeOffset>
{
  public int Compare(in DateTimeOffset x, in DateTimeOffset y)
  {
    return y.CompareTo(x);
  }
}
