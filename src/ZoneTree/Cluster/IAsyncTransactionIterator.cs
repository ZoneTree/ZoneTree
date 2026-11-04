namespace ZoneTree.Cluster;

/// <summary>An asynchronous column iterator borrowing a cluster transaction's snapshot.</summary>
/// <remarks>
/// Use the iterator sequentially and dispose it before its transaction ends.
/// </remarks>
public interface IAsyncTransactionIterator<TKey, TValue> : IAsyncDisposable
{
  /// <summary>The current key.</summary>
  TKey CurrentKey { get; }

  /// <summary>The current value.</summary>
  TValue CurrentValue { get; }

  /// <summary>Whether the iterator is positioned at a record.</summary>
  bool HasCurrent { get; }

  /// <summary>The current key-value pair.</summary>
  KeyValuePair<TKey, TValue> Current { get; }

  /// <summary>Moves to the next record; the first call starts at the beginning unless positioned.</summary>
  ValueTask<bool> NextAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Positions before the first key greater than or equal to the given key, or less
  /// than or equal to it in reverse order. Call NextAsync to read it.
  /// </summary>
  ValueTask SeekAsync(TKey key, CancellationToken cancellationToken = default);

  /// <summary>Positions before the first record. Call NextAsync to read it.</summary>
  ValueTask SeekFirstAsync(CancellationToken cancellationToken = default);
}
