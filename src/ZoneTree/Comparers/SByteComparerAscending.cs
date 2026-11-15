namespace ZoneTree.Comparers;

/// <summary>Compares signed bytes in ascending order.</summary>
public sealed class SByteComparerAscending : IRefComparer<sbyte>
{
  public int Compare(in sbyte x, in sbyte y)
  {
    return x.CompareTo(y);
  }
}
