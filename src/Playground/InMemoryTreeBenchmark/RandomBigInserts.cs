using ZoneTree.Collections;
using ZoneTree.Comparers;

namespace Playground.InMemoryTreeBenchmark;

public static class RandomBigInserts
{
  public static BigKey[] GetRandomArray(long count)
  {
    var arr = GetSortedArray(count);
    RandomIntInserts.Shuffle(arr);
    return arr;
  }

  public static BigKey[] GetSortedArray(long count)
  {
    var arr = new BigKey[count];
    for (var i = 0; i < count; ++i)
      arr[i] = new BigKey(i);
    return arr;
  }

  public static void InsertBPlusTree(BigKey[] arr)
  {
    var count = arr.Length;
    var tree = new BPlusTree<BigKey, BigKey>(new BigRefComparer(),
        BPlusTreeLockMode.NoLock);
    for (var i = 0; i < count; ++i)
    {
      var x = arr[i];
      tree.TryInsert(x, x, out _);
    }
    for (var i = 0; i < count; ++i)
    {
      var x = arr[i];
      var exists = tree.TryGetValue(x, out var val);
      if (!exists || !val.Equals(x))
        throw new Exception($"exists: {exists} ({x},{val}) != ({x},{x})");
    }
  }

  public static void InsertSortedDictionary(BigKey[] arr)
  {
    var count = arr.Length;
    var tree = new SortedDictionary<BigKey, BigKey>(new BigComparer());
    for (var i = 0; i < count; ++i)
    {
      var x = arr[i];
      tree.Add(x, x);
    }
    for (var i = 0; i < count; ++i)
    {
      var x = arr[i];
      var exists = tree.TryGetValue(x, out var val);
      if (!exists || !val.Equals(x))
        throw new Exception($"exists: {exists} ({x},{val}) != ({x},{x})");
    }
  }
}

public struct BigKey
{
  public long Key;
  public long A1;
  public long A2;
  public long A3;
  public long A4;
  public long A5;
  public long A6;
  public long A7;
  public long A8;
  public long A9;
  public long A10;
  public long A11;
  public long A12;
  public long A13;
  public long A14;
  public long A15;

  public BigKey(int key) : this()
  {
    Key = key;
  }
}

public sealed class BigComparer : IComparer<BigKey>
{
  public int Compare(BigKey x, BigKey y)
  {
    var r = x.Key - y.Key;
    if (r == 0)
      return 0;
    return r < 0 ? -1 : 1;
  }
}

public sealed class BigRefComparer : IRefComparer<BigKey>
{
  public int Compare(in BigKey x, in BigKey y)
  {
    var r = x.Key - y.Key;
    if (r == 0)
      return 0;
    return r < 0 ? -1 : 1;
  }
}
