namespace ZoneTree.Options;

/// <summary>
/// Drops records while segments are merged.
/// A filter instance serves a single merge on a single thread.
/// </summary>
/// <remarks>
/// The merge calls <see cref="Keep"/> once per distinct key in ascending key order,
/// with the newest value of the key. Deleted records never reach the filter: the merge
/// drops them, or keeps them while older records remain outside the merge.
/// Multi-part disk segments can copy a whole part without reading its records.
/// The merge asks <see cref="CanSkipPart"/> first and reports the copy with
/// <see cref="OnPartSkipped"/>; the filter does not see the records of a copied part.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IMergeFilter<TKey, TValue>
{
  /// <summary>
  /// Decides whether the merge writes the record.
  /// </summary>
  /// <param name="key">The key.</param>
  /// <param name="value">The newest value of the key.</param>
  /// <returns>true to write the record, false to drop it.</returns>
  bool Keep(in TKey key, in TValue value);

  /// <summary>
  /// Decides whether the merge may copy a whole part without passing
  /// its records to <see cref="Keep"/>.
  /// </summary>
  /// <param name="firstKey">The first key of the part.</param>
  /// <returns>true to allow the copy.</returns>
  bool CanSkipPart(in TKey firstKey);

  /// <summary>
  /// Called after the merge copied a whole part. The next record follows
  /// the last key of the copied part.
  /// </summary>
  void OnPartSkipped();
}
