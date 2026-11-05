namespace ZoneTree.Cluster;

/// <summary>
/// The keys a transaction read or wrote. Adding is a list append; duplicates are
/// removed only when the list doubles and when the keys are taken. Ordered, distinct
/// keys need no compaction or sorting, so collecting them takes linear work.
/// </summary>
/// <remarks>
/// The keys are sorted in a copy. A sort can lose an element when the comparer throws,
/// and a rollback reads the list to find every key the transaction wrote.
/// </remarks>
internal sealed class KeyList<TKey>
{
  readonly IComparer<TKey> Comparer;

  List<TKey> Keys = new();

  int CompactionThreshold = 64;

  // A negative value means a duplicate or an out-of-order key was found.
  // Otherwise only the suffix added since the last check needs comparing.
  int OrderedPrefixLength;

  public KeyList(IComparer<TKey> comparer)
  {
    Comparer = comparer;
  }

  public List<TKey> Items => Keys;

  public void Add(in TKey key)
  {
    Keys.Add(key);
    if (Keys.Count >= CompactionThreshold)
    {
      if (!AreKeysOrdered())
      {
        Keys = new List<TKey>(SortDistinct());
        OrderedPrefixLength = Keys.Count;
      }
      CompactionThreshold = Math.Max(64, Keys.Count * 2);
    }
  }

  /// <summary>
  /// Returns the distinct keys in ascending order.
  /// </summary>
  public TKey[] ToDistinctArray() => AreKeysOrdered() ? Keys.ToArray() : SortDistinct();

  bool AreKeysOrdered()
  {
    if (OrderedPrefixLength < 0)
      return false;
    for (var i = Math.Max(1, OrderedPrefixLength); i < Keys.Count; ++i)
    {
      if (Comparer.Compare(Keys[i - 1], Keys[i]) >= 0)
      {
        OrderedPrefixLength = -1;
        return false;
      }
    }
    OrderedPrefixLength = Keys.Count;
    return true;
  }

  /// <summary>
  /// Returns a sorted copy of the keys without duplicates.
  /// </summary>
  TKey[] SortDistinct()
  {
    var keys = Keys.ToArray();
    Array.Sort(keys, Comparer);
    var length = Math.Min(keys.Length, 1);
    for (var i = 1; i < keys.Length; ++i)
    {
      if (Comparer.Compare(keys[length - 1], keys[i]) != 0)
        keys[length++] = keys[i];
    }
    Array.Resize(ref keys, length);
    return keys;
  }
}
