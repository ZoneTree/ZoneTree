namespace ZoneTree.Comparers;

/// <summary>Compares floats in descending order, as float.CompareTo orders them.</summary>
public sealed class SingleComparerDescending : IRefComparer<float>
{
  public int Compare(in float x, in float y)
  {
    return y.CompareTo(x);
  }
}
