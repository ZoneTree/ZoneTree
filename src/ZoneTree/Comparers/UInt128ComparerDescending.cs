#if NET7_0_OR_GREATER
namespace ZoneTree.Comparers;

/// <summary>Compares 128-bit unsigned integers in descending order.</summary>
public sealed class UInt128ComparerDescending : IRefComparer<UInt128>
{
  public int Compare(in UInt128 x, in UInt128 y)
  {
    return y.CompareTo(x);
  }
}
#endif
