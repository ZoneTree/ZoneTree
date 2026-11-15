using System.Numerics;

namespace ZoneTree.Comparers;

/// <summary>Compares arbitrary-precision integers in descending order.</summary>
public sealed class BigIntegerComparerDescending : IRefComparer<BigInteger>
{
  public int Compare(in BigInteger x, in BigInteger y)
  {
    return y.CompareTo(x);
  }
}
