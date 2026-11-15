namespace ZoneTree.Comparers;

/// <summary>Compares floats in ascending order, as float.CompareTo orders them.</summary>
public sealed class SingleComparerAscending : IRefComparer<float>
{
  public int Compare(in float x, in float y)
  {
    return x.CompareTo(y);
  }
}
