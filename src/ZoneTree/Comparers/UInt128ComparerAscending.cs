#if NET7_0_OR_GREATER
namespace ZoneTree.Comparers;

/// <summary>Compares 128-bit unsigned integers in ascending order.</summary>
public sealed class UInt128ComparerAscending : IRefComparer<UInt128>
{
  public int Compare(in UInt128 x, in UInt128 y)
  {
    return x.CompareTo(y);
  }
}
#endif
