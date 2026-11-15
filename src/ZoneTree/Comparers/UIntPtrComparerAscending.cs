namespace ZoneTree.Comparers;

/// <summary>Compares native-sized unsigned integers in ascending order.</summary>
public sealed class UIntPtrComparerAscending : IRefComparer<nuint>
{
  public int Compare(in nuint x, in nuint y)
  {
    return x.CompareTo(y);
  }
}
