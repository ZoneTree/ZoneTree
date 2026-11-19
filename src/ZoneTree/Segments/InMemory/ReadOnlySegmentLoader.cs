using ZoneTree.Core;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.WAL;

namespace ZoneTree.Segments.InMemory;

public sealed class ReadOnlySegmentLoader<TKey, TValue>
{
  public ZoneTreeOptions<TKey, TValue> Options { get; }

  public ReadOnlySegmentLoader(
      ZoneTreeOptions<TKey, TValue> options)
  {
    Options = options;
  }

  public IReadOnlySegment<TKey, TValue> LoadReadOnlySegment(long segmentId)
  {
    var wal = Options.WriteAheadLogProvider.GetOrCreateWAL(
        segmentId,
        ZoneTree<TKey, TValue>.SegmentWalCategory,
        Options.WriteAheadLogOptions,
        Options.KeySerializer,
        Options.ValueSerializer);
    var result = wal.ReadLogEntries(false, false, true);
    if (!result.Success)
    {
      if (result.HasFoundIncompleteTailRecord)
      {
        var incompleteTailException = result.IncompleteTailRecord;
        wal.TruncateIncompleteTailRecord(incompleteTailException);
      }
      else
      {
        Options.WriteAheadLogProvider.RemoveWAL(
            segmentId,
            ZoneTree<TKey, TValue>.SegmentWalCategory);
        using var disposeWal = wal;
        throw new WriteAheadLogCorruptionException(segmentId, result.Exceptions);
      }
    }
    wal.MarkFrozen();

    (var newKeys, var newValues) = WriteAheadLogUtility
        .StableSortAndCleanUpDuplicatedKeys(
        result.Keys,
        result.Values,
        Options.Comparer);

    // The read-only segment takes the WAL from the provider in its constructor,
    // so it must be removed only afterwards. Otherwise the segment could not
    // delete the WAL files when it is dropped after a merge.
    var ros = new ReadOnlySegment<TKey, TValue>(
        segmentId,
        Options,
        newKeys,
        newValues)
    {
      MaximumOpIndex = result.MaximumOpIndex
    };
    Options.WriteAheadLogProvider.RemoveWAL(
        segmentId,
        ZoneTree<TKey, TValue>.SegmentWalCategory);
    return ros;
  }
}
