namespace ZoneTree.Transactional.Conflicts;

/// <summary>
/// The keys a transaction read or wrote. Adding is a list append; duplicates are
/// removed only when the list doubles and when the keys are taken, so a short
/// transaction allocates little and a long one stays bounded by its distinct keys.
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
      Keys = new List<TKey>(SortDistinct());
      CompactionThreshold = Math.Max(64, Keys.Count * 2);
    }
  }

  /// <summary>
  /// Returns the distinct keys in ascending order.
  /// </summary>
  public TKey[] ToDistinctArray() => SortDistinct();

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
