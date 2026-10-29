namespace ZoneTree.Transactional;

/// <summary>
/// Iterates the snapshot of a transaction.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface ITransactionIterator<TKey, TValue> : IDisposable
{
  /// <summary>
  /// The current key.
  /// </summary>
  TKey CurrentKey { get; }

  /// <summary>
  /// The current value.
  /// </summary>
  TValue CurrentValue { get; }

  /// <summary>
  /// True if the iterator is positioned at a record.
  /// </summary>
  bool HasCurrent { get; }

  /// <summary>
  /// The current key-value pair.
  /// </summary>
  KeyValuePair<TKey, TValue> Current { get; }

  /// <summary>
  /// Moves to the next record. The first call starts from the beginning
  /// unless the iterator was positioned with a seek.
  /// </summary>
  /// <returns>true if there is a record; false at the end.</returns>
  bool Next();

  /// <summary>
  /// Positions the iterator before the first key that is greater than or equal to
  /// the given key, or less than or equal to it for a reverse iterator.
  /// Call <see cref="Next"/> to read it.
  /// </summary>
  void Seek(in TKey key);

  /// <summary>
  /// Positions the iterator before the first record.
  /// Call <see cref="Next"/> to read it.
  /// </summary>
  void SeekFirst();
}
