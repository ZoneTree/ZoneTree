using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal sealed class AsyncTransactionIterator<TKey, TValue>(ITransactionIterator<TKey, TValue> iterator)
    : IAsyncTransactionIterator<TKey, TValue>
{
  public TKey CurrentKey => iterator.CurrentKey;
  public TValue CurrentValue => iterator.CurrentValue;
  public bool HasCurrent => iterator.HasCurrent;
  public KeyValuePair<TKey, TValue> Current => iterator.Current;

  // The iterator completes each step synchronously, so the steps share two completed tasks.
  static readonly Task<bool> HasNext = Task.FromResult(true);

  static readonly Task<bool> Ended = Task.FromResult(false);

  public Task<bool> NextAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return iterator.Next() ? HasNext : Ended;
  }

  public Task SeekAsync(TKey key, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    iterator.Seek(in key);
    return Task.CompletedTask;
  }

  public Task SeekFirstAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    iterator.SeekFirst();
    return Task.CompletedTask;
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
