using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>
/// Iterates a raw column through the iterator type of column views,
/// so raw handles do not expose the storage iterator.
/// </summary>
internal sealed class RawColumnIterator<TKey, TValue>(IZoneTreeIterator<TKey, TValue> iterator)
    : ITransactionIterator<TKey, TValue>
{
  public TKey CurrentKey => iterator.CurrentKey;
  public TValue CurrentValue => iterator.CurrentValue;
  public bool HasCurrent => iterator.HasCurrent;
  public KeyValuePair<TKey, TValue> Current => iterator.Current;
  public bool Next() => iterator.Next();
  public void Seek(in TKey key) => iterator.Seek(in key);
  public void SeekFirst() => iterator.SeekFirst();
  public void Dispose() => iterator.Dispose();
}
