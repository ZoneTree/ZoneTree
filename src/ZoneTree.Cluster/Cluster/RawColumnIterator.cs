using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>
/// Iterates a raw column through the iterator type of column views, so raw handles do not
/// expose the storage iterator.
/// </summary>
/// <remarks>
/// Dropping the column disposes the iterator, and its later steps report the drop.
/// </remarks>
internal sealed class RawColumnIterator<TKey, TValue> : ITransactionIterator<TKey, TValue>, IColumnDependent
{
  readonly Column Column;
  readonly IZoneTreeIterator<TKey, TValue> Iterator;
  int IsDisposed;

  internal RawColumnIterator(Column column, IZoneTreeIterator<TKey, TValue> iterator)
  {
    Column = column;
    Iterator = iterator;
    column.AddDependent(this);
  }

  public TKey CurrentKey => Iterator.CurrentKey;
  public TValue CurrentValue => Iterator.CurrentValue;
  public bool HasCurrent => Iterator.HasCurrent;
  public KeyValuePair<TKey, TValue> Current => Iterator.Current;

  public bool Next()
  {
    EnsureUsable();
    return Iterator.Next();
  }

  public void Seek(in TKey key)
  {
    EnsureUsable();
    Iterator.Seek(in key);
  }

  public void SeekFirst()
  {
    EnsureUsable();
    Iterator.SeekFirst();
  }

  void EnsureUsable()
  {
    Column.ThrowIfDropped();
    ObjectDisposedException.ThrowIf(Volatile.Read(ref IsDisposed) != 0, this);
  }

  void IColumnDependent.OnColumnDropped() => Dispose();

  public void Dispose()
  {
    if (Interlocked.Exchange(ref IsDisposed, 1) != 0)
      return;
    Column.RemoveDependent(this);
    Iterator.Dispose();
  }
}
