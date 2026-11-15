using System.Numerics;

namespace ZoneTree.Comparers;

/// <summary>Compares arbitrary-precision integers in ascending order.</summary>
public sealed class BigIntegerComparerAscending : IRefComparer<BigInteger>
{
  public int Compare(in BigInteger x, in BigInteger y)
  {
    return x.CompareTo(y);
  }
}
