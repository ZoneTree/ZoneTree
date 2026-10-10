using System.Diagnostics;
using ZoneTree.Collections;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Segments.Disk;
using ZoneTree.Segments.MultiPart;

namespace ZoneTree.Core;

public sealed partial class ZoneTree<TKey, TValue> : IZoneTree<TKey, TValue>, IZoneTreeMaintenance<TKey, TValue>
{
  public Thread StartBottomSegmentsMergeOperation(int fromIndex, int toIndex)
  {
    if (fromIndex < 0 || fromIndex >= toIndex)
    {
      throw new InvalidMergeRangeException(fromIndex, toIndex);
    }
    if (IsBottomSegmentsMerging)
    {
      OnBottomSegmentsMergeOperationEnded?.Invoke(this, MergeResult.ANOTHER_MERGE_IS_RUNNING);
      return null;
    }

    OnBottomSegmentsMergeOperationStarted?.Invoke(this);
    var thread = new Thread(() => StartBottomSegmentsMergeOperationInternal(fromIndex, toIndex));
    thread.Start();
    return thread;
  }

  void StartBottomSegmentsMergeOperationInternal(int from, int to)
  {
    if (IsBottomSegmentsMerging)
    {
      OnBottomSegmentsMergeOperationEnded?.Invoke(this, MergeResult.ANOTHER_MERGE_IS_RUNNING);
      return;
    }
    IsCancelBottomSegmentsMergeRequested = false;
    lock (LongBottomSegmentsMergerLock)
    {
      try
      {
        if (IsBottomSegmentsMerging)
        {
          OnBottomSegmentsMergeOperationEnded?.Invoke(this, MergeResult.ANOTHER_MERGE_IS_RUNNING);
          return;
        }
        IsBottomSegmentsMerging = true;
        var mergeResult = MergeBottomSegmentsInternal(from, to);
        IsBottomSegmentsMerging = false;
        OnBottomSegmentsMergeOperationEnded?.Invoke(this, mergeResult);

      }
      catch (Exception e)
      {
        Logger.LogError(e);
        OnBottomSegmentsMergeOperationEnded?.Invoke(this, MergeResult.FAILURE);
      }
      finally
      {
        IsBottomSegmentsMerging = false;
      }
    }
  }

  public void TryCancelBottomSegmentsMergeOperation()
  {
    IsCancelBottomSegmentsMergeRequested = true;
  }

  MergeResult MergeBottomSegmentsInternal(int from, int to)
  {
    var stopwatch = new Stopwatch();
    stopwatch.Start();

    var bottomSegments = BottomSegmentQueue.ToLastInFirstArray();

    if (to >= bottomSegments.Count)
      to = bottomSegments.Count - 1;
    var writeDeletedValues = to < bottomSegments.Count - 1;

    var selectedBottomSegments = bottomSegments
        .Skip(from)
        .Take(to - from + 1)
        .ToArray();
    if (selectedBottomSegments.Length == 0)
      return MergeResult.NOTHING_TO_MERGE;
    var selectedBottomSegmentIds = selectedBottomSegments
        .Select(x => x.SegmentId)
        .ToArray();
    var mergingSegments = selectedBottomSegments
        .Select(x => x.GetSeekableIterator())
        .ToArray();
    to = from + mergingSegments.Length - 1;
    var bottomDiskSegment = selectedBottomSegments[^1];
    Logger.LogTrace($"Bottom Segments Merge started." +
        $" from: {from} - to: {to} out of: {bottomSegments.Count} ");

    if (IsCancelBottomSegmentsMergeRequested)
    {
      // Do not remove null assignments because of GC issue!
      bottomSegments = null;
      selectedBottomSegments = null;
      selectedBottomSegmentIds = null;
      mergingSegments = null;
      bottomDiskSegment = null;
      return MergeResult.CANCELLED_BY_USER;
    }

    var enableMultiPartDiskSegment =
        Options.DiskSegmentOptions.DiskSegmentMode == DiskSegmentMode.MultiPartDiskSegment;

    var len = mergingSegments.Length;
    var bottomIndex = len - 1;

    using IDiskSegmentCreator<TKey, TValue> diskSegmentCreator =
        enableMultiPartDiskSegment ?
        new MultiPartDiskSegmentCreator<TKey, TValue>(Options, IncrementalIdProvider) :
        new DiskSegmentCreator<TKey, TValue>(Options, IncrementalIdProvider);

    var heap = new FixedSizeMinHeap<HeapEntry<TKey, TValue>>(len + 1, MinHeapEntryComparer);

    void fillHeap()
    {
      for (int i = 0; i < len; i++)
      {
        var s = mergingSegments[i];
        if (!s.Next())
          continue;
        var key = s.CurrentKey;
        var value = s.CurrentValue;
        var entry = new HeapEntry<TKey, TValue>(key, value, i);
        heap.Insert(entry);
      }
    }

    int minSegmentIndex = 0;

    void skipElement()
    {
      var minSegment = mergingSegments[minSegmentIndex];
      if (minSegment.Next())
      {
        var key = minSegment.CurrentKey;
        var value = minSegment.CurrentValue;
        heap.ReplaceMin(new HeapEntry<TKey, TValue>(key, value, minSegmentIndex));
      }
      else
      {
        heap.RemoveMin();
      }
    }
    fillHeap();
    var comparer = Options.Comparer;
    var hasPrev = false;
    TKey prevKey = default;

    var lastKeysOfEveryPart = bottomDiskSegment.GetLastKeysOfEveryPart();
    var lastValuesOfEveryPart = bottomDiskSegment.GetLastValuesOfEveryPart();
    var diskSegmentMinimumRecordCount = Options.DiskSegmentOptions.MinimumRecordCount;
    var isRangeDeleted = Options.IsRangeDeleted;

    var dropCount = 0;
    var skipCount = 0;
    while (heap.Count > 0)
    {
      if (IsCancelBottomSegmentsMergeRequested)
      {
        try
        {
          diskSegmentCreator.DropDiskSegment();
        }
        catch (Exception e)
        {
          Logger.LogError(e);
          OnCanNotDropDiskSegmentCreator?.Invoke(diskSegmentCreator, e);
        }
        // Do not remove null assignments because of GC issue!
        bottomSegments = null;
        selectedBottomSegments = null;
        selectedBottomSegmentIds = null;
        mergingSegments = null;
        bottomDiskSegment = null;
        heap = null;
        return MergeResult.CANCELLED_BY_USER;
      }

      var minEntry = heap.MinValue;
      minSegmentIndex = minEntry.SegmentIndex;

      // Drop deleted parts before deletion/duplicate handling consumes their first record.
      if (!writeDeletedValues && isRangeDeleted != null &&
          minSegmentIndex == bottomIndex &&
          mergingSegments[bottomIndex].IsBeginningOfAPart)
      {
        var partIndex = mergingSegments[bottomIndex].GetPartIndex();
        if (isRangeDeleted(minEntry.Key, minEntry.Value,
            lastKeysOfEveryPart[partIndex], lastValuesOfEveryPart[partIndex]))
        {
          mergingSegments[bottomIndex].Skip(bottomDiskSegment.GetPart(partIndex).Length - 1);
          skipElement();
          continue;
        }
      }

      // ignore deleted entries if writeDeletedValues is false.
      if (!writeDeletedValues && IsDeleted(minEntry.Key, minEntry.Value))
      {
        skipElement();
        prevKey = minEntry.Key;
        hasPrev = true;
        continue;
      }

      if (hasPrev && comparer.Compare(minEntry.Key, prevKey) == 0)
      {
        skipElement();
        continue;
      }

      prevKey = minEntry.Key;
      hasPrev = true;
      var isDiskSegmentKey = minSegmentIndex == bottomIndex;
      var iteratorPosition = IteratorPosition.None;
      var currentPartIndex = -1;
      if (isDiskSegmentKey)
      {
        var diskIterator = mergingSegments[minSegmentIndex];
        iteratorPosition =
            diskIterator.IsBeginningOfAPart ?
            IteratorPosition.BeginningOfAPart :
            diskIterator.IsEndOfAPart ?
            IteratorPosition.EndOfAPart :
            IteratorPosition.MiddleOfAPart;
        currentPartIndex = diskIterator.GetPartIndex();
      }

      // skip a part without merge if possible
      if (enableMultiPartDiskSegment &&
          isDiskSegmentKey &&
          iteratorPosition == IteratorPosition.BeginningOfAPart)
      {
        var part = bottomDiskSegment
            .GetPart(currentPartIndex);
        if (part.Length > diskSegmentMinimumRecordCount &&
            diskSegmentCreator.CanSkipCurrentPart)
        {
          var lastKey = lastKeysOfEveryPart[currentPartIndex];
          var islastKeySmallerThanAllOtherKeys = true;
          var heapKeys = heap.Keys;
          var heapKeysLen = heapKeys.Length;
          for (int i = 0; i < heapKeysLen; i++)
          {
            var s = heapKeys[i];
            if (s.SegmentIndex == minSegmentIndex)
              continue;
            var key = s.Key;
            if (comparer.Compare(lastKey, key) >= 0)
            {
              islastKeySmallerThanAllOtherKeys = false;
              break;
            }
          }
          if (islastKeySmallerThanAllOtherKeys)
          {
            diskSegmentCreator.Append(
                part,
                minEntry.Key,
                lastKey,
                minEntry.Value,
                lastValuesOfEveryPart[currentPartIndex]);
            mergingSegments[bottomIndex].Skip(part.Length - 2);
            prevKey = lastKey;
            skipElement();
            ++skipCount;
            continue;
          }
        }
        ++dropCount;
        Logger.LogTrace(new LogMergerDrop(part.SegmentId, dropCount, skipCount));

      }

      diskSegmentCreator.Append(minEntry.Key, minEntry.Value, iteratorPosition);
      skipElement();
    }

    var newDiskSegment = diskSegmentCreator.CreateReadOnlyDiskSegment();
    newDiskSegment.DropFailureReporter = ReportDropFailure;
    OnDiskSegmentCreated?.Invoke(this, newDiskSegment, true);
    lock (ShortMergerLock)
    {
      bottomSegments = BottomSegmentQueue.ToLastInFirstArray();
      var bottomSegmentsLength = bottomSegments.Count;
      var currentFrom = IndexOfContiguousSegmentIds(
          bottomSegments,
          selectedBottomSegmentIds);
      if (currentFrom < 0)
      {
        try
        {
          newDiskSegment.Drop(diskSegmentCreator.AppendedPartSegmentIds);
        }
        catch (Exception e)
        {
          Logger.LogError(e);
          OnCanNotDropDiskSegment?.Invoke(newDiskSegment, e);
        }

        // Do not remove null assignments because of GC issue!
        bottomSegments = null;
        selectedBottomSegments = null;
        selectedBottomSegmentIds = null;
        mergingSegments = null;
        bottomDiskSegment = null;
        heap = null;
        newDiskSegment = null;
        return MergeResult.FAILURE;
      }
      var currentTo = currentFrom + selectedBottomSegmentIds.Length - 1;
      var queue = new Queue<IDiskSegment<TKey, TValue>>();
      for (var i = 0; i < bottomSegmentsLength; ++i)
      {
        if (i > currentFrom && i <= currentTo)
        {
          MetaWal.DeleteBottomSegment(bottomSegments[i].SegmentId);
          continue;
        }
        if (i == currentFrom)
        {
          MetaWal.InsertBottomSegment(newDiskSegment.SegmentId, i);
          MetaWal.DeleteBottomSegment(bottomSegments[i].SegmentId);
          queue.Enqueue(newDiskSegment);
        }
        else
        {
          queue.Enqueue(bottomSegments[i]);
        }
      }
      var newQueue = new SingleProducerSingleConsumerQueue<IDiskSegment<TKey, TValue>>(queue.Reverse());
      BottomSegmentQueue = newQueue;

      for (var i = currentFrom; i <= currentTo; ++i)
      {
        var diskSegmentToDrop = bottomSegments[i];
        try
        {
          diskSegmentToDrop.Drop(diskSegmentCreator.AppendedPartSegmentIds);
        }
        catch (Exception e)
        {
          Logger.LogError(e);
          OnCanNotDropDiskSegment?.Invoke(diskSegmentToDrop, e);
        }
      }
    }

    TotalBottomSegmentsMergeSkipCount += skipCount;
    TotalBottomSegmentsMergeDropCount += dropCount;
    Logger.LogTrace(
        new LogMergerSuccess(
            dropCount,
            skipCount,
            stopwatch.ElapsedMilliseconds,
            TotalBottomSegmentsMergeDropCount,
            TotalBottomSegmentsMergeSkipCount));

    // Do not remove null assignments below and anywhere in this function!
    // GC does not collect local variables,
    // when this method is called by another thread.
    bottomSegments = null;
    selectedBottomSegments = null;
    selectedBottomSegmentIds = null;
    mergingSegments = null;
    bottomDiskSegment = null;
    heap = null;
    OnDiskSegmentActivated?.Invoke(this, newDiskSegment, true);
    newDiskSegment = null;
    return MergeResult.SUCCESS;
  }

  static int IndexOfContiguousSegmentIds(
      List<IDiskSegment<TKey, TValue>> segments,
      long[] segmentIds)
  {
    if (segmentIds.Length == 0 || segmentIds.Length > segments.Count)
      return -1;

    var lastStartIndex = segments.Count - segmentIds.Length;
    for (var i = 0; i <= lastStartIndex; ++i)
    {
      var isMatch = true;
      for (var j = 0; j < segmentIds.Length; ++j)
      {
        if (segments[i + j].SegmentId == segmentIds[j])
          continue;
        isMatch = false;
        break;
      }
      if (isMatch)
        return i;
    }
    return -1;
  }

  int TotalBottomSegmentsMergeSkipCount;
  int TotalBottomSegmentsMergeDropCount;
}
