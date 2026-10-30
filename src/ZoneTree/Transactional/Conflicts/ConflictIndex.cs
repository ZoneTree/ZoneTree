using ZoneTree.Comparers;

namespace ZoneTree.Transactional.Conflicts;

/// <summary>
/// Maps each key written by a recent commit to the timestamp of its latest commit.
/// A transaction conflicts with the keys committed after its snapshot timestamp.
/// </summary>
/// <remarks>
/// A key is removed once every active snapshot includes its commit,
/// because such a commit cannot conflict with any active or future transaction.
/// </remarks>
internal sealed class ConflictIndex<TKey>
{
  sealed class Entry
  {
    public TKey Key;

    public long CommitTimestamp;
  }

  sealed class EntryComparer(IRefComparer<TKey> comparer) : IComparer<Entry>
  {
    public int Compare(Entry x, Entry y) => comparer.Compare(in x.Key, in y.Key);
  }

  readonly Lock SyncRoot = new();

  readonly IRefComparer<TKey> Comparer;

  readonly SortedSet<Entry> Entries;

  public ConflictIndex(IRefComparer<TKey> comparer)
  {
    Comparer = comparer;
    Entries = new SortedSet<Entry>(new EntryComparer(comparer));
  }

  public int Count
  {
    get
    {
      lock (SyncRoot)
        return Entries.Count;
    }
  }

  public bool HasCommitAfter(in TKey key, long timestamp)
  {
    lock (SyncRoot)
      return HasCommitAfterNoLock(in key, timestamp);
  }

  public bool HasCommitAfter(IEnumerable<TKey> keys, long timestamp)
  {
    lock (SyncRoot)
    {
      if (Entries.Count == 0)
        return false;
      foreach (var key in keys)
      {
        if (HasCommitAfterNoLock(in key, timestamp))
          return true;
      }
      return false;
    }
  }

  public bool HasCommitAfter(IReadOnlyList<KeyRange<TKey>> ranges, long timestamp)
  {
    lock (SyncRoot)
    {
      if (Entries.Count == 0)
        return false;
      var count = ranges.Count;
      for (var i = 0; i < count; ++i)
      {
        var range = ranges[i];
        var low = range.HasLow ? new Entry { Key = range.Low } : Entries.Min;
        var high = range.HasHigh ? new Entry { Key = range.High } : Entries.Max;
        if (Comparer.Compare(in low.Key, in high.Key) > 0)
          continue;
        foreach (var entry in Entries.GetViewBetween(low, high))
        {
          if (entry.CommitTimestamp > timestamp)
            return true;
        }
      }
      return false;
    }
  }

  bool HasCommitAfterNoLock(in TKey key, long timestamp)
  {
    return Entries.TryGetValue(new Entry { Key = key }, out var entry) &&
        entry.CommitTimestamp > timestamp;
  }

  public void Add(TKey[] keys, long commitTimestamp)
  {
    lock (SyncRoot)
    {
      foreach (var key in keys)
      {
        if (Entries.TryGetValue(new Entry { Key = key }, out var entry))
          entry.CommitTimestamp = commitTimestamp;
        else
          Entries.Add(new Entry { Key = key, CommitTimestamp = commitTimestamp });
      }
    }
  }

  /// <summary>
  /// Removes the keys whose latest commit is the given one.
  /// </summary>
  public void Remove(TKey[] keys, long commitTimestamp)
  {
    lock (SyncRoot)
    {
      foreach (var key in keys)
      {
        if (Entries.TryGetValue(new Entry { Key = key }, out var entry) &&
            entry.CommitTimestamp == commitTimestamp)
          Entries.Remove(entry);
      }
    }
  }
}
