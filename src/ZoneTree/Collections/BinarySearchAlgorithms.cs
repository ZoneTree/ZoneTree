using System.Runtime.CompilerServices;
using ZoneTree.Comparers;

namespace ZoneTree.Collections;

public static class BinarySearchAlgorithms
{
  public delegate TKey KeyByIndex<TKey>(long index);

  public delegate int CompareKeyByIndex(int index);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int FirstGreaterOrEqualPosition(
      CompareKeyByIndex compareKeyByIndex,
      int left,
      int right)
  {
    int result = right + 1;
    while (left <= right)
    {
      int mid = left + (right - left) / 2;

      if (compareKeyByIndex(mid) >= 0)
      {
        result = mid;
        right = mid - 1;
      }
      else
      {
        left = mid + 1;
      }
    }

    return result;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int FirstGreaterOrEqualPosition<TKey>(
      IReadOnlyList<TKey> list,
      int left,
      int right,
      IRefComparer<TKey> comparer,
      in TKey key)
  {
    int result = right + 1;
    while (left <= right)
    {
      int mid = left + (right - left) / 2;

      if (comparer.Compare(list[mid], in key) >= 0)
      {
        result = mid;
        right = mid - 1;
      }
      else
      {
        left = mid + 1;
      }
    }

    return result;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static long FirstGreaterOrEqualPosition<TKey>(
      KeyByIndex<TKey> keyByIndex,
      long left,
      long right,
      IRefComparer<TKey> comparer,
      in TKey key)
  {
    long result = right + 1;

    while (left <= right)
    {
      long mid = left + (right - left) / 2;

      if (comparer.Compare(keyByIndex(mid), in key) >= 0)
      {
        result = mid;
        right = mid - 1;
      }
      else
      {
        left = mid + 1;
      }
    }

    return result;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int LastSmallerOrEqualPosition(
      CompareKeyByIndex compareKeyByIndex,
      int left,
      int right)
  {
    int result = -1;

    while (left <= right)
    {
      int mid = left + (right - left) / 2;

      if (compareKeyByIndex(mid) <= 0)
      {
        result = mid;
        left = mid + 1;
      }
      else
      {
        right = mid - 1;
      }
    }

    return result;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int LastSmallerOrEqualPosition<TKey>(
      IReadOnlyList<TKey> list,
      int left,
      int right,
      IRefComparer<TKey> comparer,
      in TKey key)
  {
    int result = -1;

    while (left <= right)
    {
      int mid = left + (right - left) / 2;

      if (comparer.Compare(list[mid], in key) <= 0)
      {
        result = mid;
        left = mid + 1;
      }
      else
      {
        right = mid - 1;
      }
    }

    return result;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static long LastSmallerOrEqualPosition<TKey>(
      KeyByIndex<TKey> keyByIndex,
      long left,
      long right,
      IRefComparer<TKey> comparer,
      in TKey key)
  {
    long result = -1;

    while (left <= right)
    {
      var mid = left + (right - left) / 2;
      if (comparer.Compare(keyByIndex(mid), in key) <= 0)
      {
        result = mid;
        left = mid + 1;
      }
      else
      {
        right = mid - 1;
      }
    }

    return result;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int BinarySearch<TKey>(
      IReadOnlyList<TKey> list,
      int left,
      int right,
      IRefComparer<TKey> comparer,
      in TKey key)
  {
    while (left <= right)
    {
      var mid = left + (right - left) / 2;
      var res = comparer.Compare(list[mid], in key);
      if (res == 0)
        return mid;
      if (res < 0)
        left = mid + 1;
      else
        right = mid - 1;
    }
    return -1;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static long BinarySearch<TKey>(
      KeyByIndex<TKey> keyByIndex,
      long left,
      long right,
      IRefComparer<TKey> comparer,
      in TKey key)
  {
    while (left <= right)
    {
      var mid = left + (right - left) / 2;
      var res = comparer.Compare(keyByIndex(mid), in key);
      if (res == 0)
        return mid;
      if (res < 0)
        left = mid + 1;
      else
        right = mid - 1;
    }
    return -1;
  }
}
