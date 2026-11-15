namespace ZoneTree.Comparers;

/// <summary>Compares half-precision floats in ascending order, as Half.CompareTo orders them.</summary>
public sealed class HalfComparerAscending : IRefComparer<Half>
{
  public int Compare(in Half x, in Half y)
  {
    return x.CompareTo(y);
  }
}
