#if NET7_0_OR_GREATER
namespace ZoneTree.Comparers;

/// <summary>Compares 128-bit signed integers in descending order.</summary>
public sealed class Int128ComparerDescending : IRefComparer<Int128>
{
  public int Compare(in Int128 x, in Int128 y)
  {
    return y.CompareTo(x);
  }
}
#endif
