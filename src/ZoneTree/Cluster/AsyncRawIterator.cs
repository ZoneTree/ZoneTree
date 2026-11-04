namespace ZoneTree.Cluster;

internal sealed class AsyncRawIterator<TKey, TValue>(IZoneTreeIterator<TKey, TValue> iterator)
    : IAsyncIterator<TKey, TValue>
{
  public TKey CurrentKey => iterator.CurrentKey;
  public TValue CurrentValue => iterator.CurrentValue;
  public bool HasCurrent => iterator.HasCurrent;
  public KeyValuePair<TKey, TValue> Current => iterator.Current;

  public ValueTask<bool> NextAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(iterator.Next());
  }

  public ValueTask SeekAsync(TKey key, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    iterator.Seek(in key);
    return ValueTask.CompletedTask;
  }

  public ValueTask SeekFirstAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    iterator.SeekFirst();
    return ValueTask.CompletedTask;
  }

  public ValueTask DisposeAsync()
  {
    iterator.Dispose();
    return ValueTask.CompletedTask;
  }
}
