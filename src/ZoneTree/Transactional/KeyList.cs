namespace ZoneTree.Transactional;

/// <summary>
/// The keys a transaction read or wrote. Adding is a list append; duplicates are
/// removed only when the list doubles and when the keys are taken, so a short
/// transaction allocates little and a long one stays bounded by its distinct keys.
/// </summary>
internal sealed class KeyList<TKey>
{
  readonly IComparer<TKey> Comparer;

  readonly List<TKey> Keys = new();

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
      Compact();
      CompactionThreshold = Math.Max(64, Keys.Count * 2);
    }
  }

  /// <summary>
  /// Returns the distinct keys in ascending order.
  /// </summary>
  public TKey[] ToDistinctArray()
  {
    Compact();
    return Keys.ToArray();
  }

  void Compact()
  {
    if (Keys.Count < 2)
      return;
    Keys.Sort(Comparer);
    var length = 1;
    for (var i = 1; i < Keys.Count; ++i)
    {
      if (Comparer.Compare(Keys[length - 1], Keys[i]) != 0)
        Keys[length++] = Keys[i];
    }
    Keys.RemoveRange(length, Keys.Count - length);
  }
}
