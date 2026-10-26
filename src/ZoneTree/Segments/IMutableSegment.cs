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

  IIncrementalIdProvider OpIndexProvider { get; }
}
