namespace ZoneTree;

/// <summary>
/// What a <see cref="ISegmentRecordVisitor{TKey, TValue}"/> does after a record.
/// </summary>
public enum SegmentVisitAction : byte
{
  /// <summary>
  /// Visit the next record of the same segment.
  /// </summary>
  NextRecord,

  /// <summary>
  /// Skip the rest of the segment and continue with the next older segment.
  /// </summary>
  NextSegment,

  /// <summary>
  /// Stop the visit.
  /// </summary>
  Stop,
}

/// <summary>
/// Visits the stored records of a ZoneTree segment by segment.
/// See <see cref="IZoneTree{TKey, TValue}.VisitSegments{TVisitor}"/>.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface ISegmentRecordVisitor<TKey, TValue>
{
  /// <summary>
  /// Visits a stored record. Deleted records are visited too.
  /// </summary>
  /// <param name="key">The key.</param>
  /// <param name="value">The stored value.</param>
  /// <returns>What to visit next.</returns>
  SegmentVisitAction Visit(in TKey key, in TValue value);
}
