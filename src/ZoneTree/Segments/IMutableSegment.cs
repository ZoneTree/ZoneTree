using ZoneTree.Collections;
using ZoneTree.Collections.BTree;
using ZoneTree.Core;

namespace ZoneTree.Segments;

public interface IMutableSegment<TKey, TValue> : IReadOnlySegment<TKey, TValue>
{
  /// <summary>
  /// Frozen segments prevents new write requests.
  /// It is the transition stage before moving to read only layer.
  /// </summary>
  bool IsFrozen { get; }

  AddOrUpdateResult Upsert(in TKey key, in TValue value, out long opIndex);

  AddOrUpdateResult Upsert(
      in TKey key,
      GetValueDelegate<TKey, TValue> valueGetter,
      out long opIndex);

  AddOrUpdateResult Delete(in TKey key, out long opIndex);

  void Freeze();

  /// <summary>
  /// Syncs the write ahead log records of the completed writes to the disk.
  /// </summary>
  Task SyncWALAsync();

  /// <summary>
  /// Visits the records with keys greater than or equal to the given
  /// key in ascending order, without copying them like an iterator does.
  /// </summary>
  /// <returns>
  /// The action that ended the visit, or
  /// <see cref="SegmentVisitAction.NextSegment"/> after the last record.
  /// </returns>
  SegmentVisitAction Visit<TVisitor>(in TKey key, ref TVisitor visitor)
      where TVisitor : struct, ISegmentRecordVisitor<TKey, TValue>;

  IIncrementalIdProvider OpIndexProvider { get; }
}
