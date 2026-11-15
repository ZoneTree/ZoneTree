namespace ZoneTree.Comparers;

/// <summary>Compares signed bytes in descending order.</summary>
public sealed class SByteComparerDescending : IRefComparer<sbyte>
{
  public int Compare(in sbyte x, in sbyte y)
  {
    return y.CompareTo(x);
  }
}
