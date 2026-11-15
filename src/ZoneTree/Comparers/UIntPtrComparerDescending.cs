namespace ZoneTree.Comparers;

/// <summary>Compares native-sized unsigned integers in descending order.</summary>
public sealed class UIntPtrComparerDescending : IRefComparer<nuint>
{
  public int Compare(in nuint x, in nuint y)
  {
    return y.CompareTo(x);
  }
}
