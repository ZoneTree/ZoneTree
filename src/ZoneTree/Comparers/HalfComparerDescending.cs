namespace ZoneTree.Comparers;

/// <summary>Compares half-precision floats in descending order, as Half.CompareTo orders them.</summary>
public sealed class HalfComparerDescending : IRefComparer<Half>
{
  public int Compare(in Half x, in Half y)
  {
    return y.CompareTo(x);
  }
}
