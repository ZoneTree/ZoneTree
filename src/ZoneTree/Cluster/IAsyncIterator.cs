namespace ZoneTree.Cluster;

/// <summary>An asynchronous iterator over a raw column.</summary>
public interface IAsyncIterator<TKey, TValue> : IAsyncDisposable
{
  TKey CurrentKey { get; }
  TValue CurrentValue { get; }
  bool HasCurrent { get; }
  KeyValuePair<TKey, TValue> Current { get; }
  ValueTask<bool> NextAsync(CancellationToken cancellationToken = default);
  ValueTask SeekAsync(TKey key, CancellationToken cancellationToken = default);
  ValueTask SeekFirstAsync(CancellationToken cancellationToken = default);
}
