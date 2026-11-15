#if NET7_0_OR_GREATER
namespace ZoneTree.Comparers;

/// <summary>Compares 128-bit signed integers in ascending order.</summary>
public sealed class Int128ComparerAscending : IRefComparer<Int128>
{
  public int Compare(in Int128 x, in Int128 y)
  {
    return x.CompareTo(y);
  }
}
#endif
