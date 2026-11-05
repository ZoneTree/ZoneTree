using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal sealed class AsyncTransactionIterator<TKey, TValue>(ITransactionIterator<TKey, TValue> iterator)
    : IAsyncTransactionIterator<TKey, TValue>
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

internal sealed class AsyncRange<TKey, TValue>(
    IEnumerable<KeyValuePair<TKey, TValue>> records, CancellationToken rangeCancellation)
    : IAsyncEnumerable<KeyValuePair<TKey, TValue>>
{
  public IAsyncEnumerator<KeyValuePair<TKey, TValue>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
      => new Enumerator(records.GetEnumerator(), rangeCancellation, cancellationToken);

  sealed class Enumerator(IEnumerator<KeyValuePair<TKey, TValue>> records,
      CancellationToken rangeCancellation, CancellationToken enumerationCancellation)
      : IAsyncEnumerator<KeyValuePair<TKey, TValue>>
  {
    public KeyValuePair<TKey, TValue> Current => records.Current;
    public ValueTask<bool> MoveNextAsync()
    {
      rangeCancellation.ThrowIfCancellationRequested();
      enumerationCancellation.ThrowIfCancellationRequested();
      return new(records.MoveNext());
    }

    public ValueTask DisposeAsync()
    {
      records.Dispose();
      return ValueTask.CompletedTask;
    }
  }
}
