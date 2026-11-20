using System.Diagnostics;
using ZoneTree.Collections;
using ZoneTree.Exceptions;
using ZoneTree.Logging;
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

    // Reset before the merge thread starts.
    // Resetting it on the merge thread would discard a cancellation
    // requested between this call and the start of the thread.
    IsCancelBottomSegmentsMergeRequested = false;
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
        Logger.LogError("Merging the bottom segments failed.", e);
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

    if (to >= bottomSegments.Length)
      to = bottomSegments.Length - 1;
    var writeDeletedValues = to < bottomSegments.Length - 1;

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
    if (Logger.IsEnabled(ZoneTreeLogLevel.Trace))
      Logger.LogTrace($"Bottom segments merge started: segments {from} to {to} of {bottomSegments.Length}.");

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
        Options.DiskSegmentOptions.Mode == DiskSegmentMode.MultiPartDiskSegment;

    var len = mergingSegments.Length;
    var bottomIndex = len - 1;

    using IDiskSegmentCreator<TKey, TValue> diskSegmentCreator =
        enableMultiPartDiskSegment ?
        new MultiPartDiskSegmentCreator<TKey, TValue>(Options, IncrementalIdProvider) :
        new DiskSegmentCreator<TKey, TValue>(Options, IncrementalIdProvider);

    // The heap holds keys only. Each segment's iterator stays on the record its heap entry holds, so the value is
    // read from the iterator once the record is the newest of its key. A record that a newer segment replaces is
    // never deserialized.
    var heap = new FixedSizeMinHeap<HeapEntry<TKey, TValue>>(len + 1, MinHeapEntryComparer);

    void fillHeap()
    {
      for (int i = 0; i < len; i++)
      {
        var s = mergingSegments[i];
        if (!s.Next())
          continue;
        heap.Insert(new HeapEntry<TKey, TValue>(s.CurrentKey, default, i));
      }
    }

    int minSegmentIndex = 0;

    void skipElement()
    {
      var minSegment = mergingSegments[minSegmentIndex];
      if (minSegment.Next())
        heap.ReplaceMin(new HeapEntry<TKey, TValue>(minSegment.CurrentKey, default, minSegmentIndex));
      else
        heap.RemoveMin();
    }
    fillHeap();
    var comparer = Options.Comparer;
    var hasPrev = false;
    TKey prevKey = default;

    var lastKeysOfEveryPart = bottomDiskSegment.GetLastKeysOfEveryPart();
    var lastValuesOfEveryPart = bottomDiskSegment.GetLastValuesOfEveryPart();
    var diskSegmentMinimumRecordCount = Options.DiskSegmentOptions.MinimumRecordCount;
    var isRangeDeleted = Options.IsRangeDeleted;
    var mergeFilter = Options.MergeFilterFactory?.Invoke(new MergeFilterContext
    {
      IncludesOldestRecords = !writeDeletedValues,
    });

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
          Logger.LogError("Dropping the unfinished disk segment of a cancelled bottom segments merge failed. Its files can stay on disk.", e);
          OnCannotDropDiskSegmentCreator?.Invoke(diskSegmentCreator, e);
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

      var minEntry = heap.Min;
      minSegmentIndex = minEntry.SegmentIndex;
      var minSegment = mergingSegments[minSegmentIndex];

      // Drop deleted parts before deletion/duplicate handling consumes their first record.
      // A part's first value comes from the part's metadata, so reading it does not deserialize the part.
      if (!writeDeletedValues && isRangeDeleted != null &&
          minSegmentIndex == bottomIndex &&
          minSegment.IsBeginningOfAPart)
      {
        var partIndex = minSegment.GetPartIndex();
        if (isRangeDeleted(minEntry.Key, minSegment.CurrentValue,
            lastKeysOfEveryPart[partIndex], lastValuesOfEveryPart[partIndex]))
        {
          minSegment.Skip(bottomDiskSegment.GetPart(partIndex).Length - 1);
          skipElement();
          continue;
        }
      }

      // The newest record of a key comes first, so a record with the previous key is an older duplicate. Checking
      // that before deletion skips the same records as checking it after, without reading their values.
      if (hasPrev && comparer.Compare(minEntry.Key, prevKey) == 0)
      {
        skipElement();
        continue;
      }

      var value = minSegment.CurrentValue;

      // ignore deleted entries if writeDeletedValues is false.
      if (!writeDeletedValues && IsDeleted(minEntry.Key, value))
      {
        skipElement();
        prevKey = minEntry.Key;
        hasPrev = true;
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
            diskSegmentCreator.CanSkipCurrentPart &&
            (mergeFilter == null || mergeFilter.CanSkipPart(minEntry.Key)))
        {
          var lastKey = lastKeysOfEveryPart[currentPartIndex];
          var islastKeySmallerThanAllOtherKeys = true;
          var heapItems = heap.Items;
          var heapItemsLen = heapItems.Length;
          for (int i = 0; i < heapItemsLen; i++)
          {
            var s = heapItems[i];
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
                value,
                lastValuesOfEveryPart[currentPartIndex]);
            mergingSegments[bottomIndex].Skip(part.Length - 2);
            prevKey = lastKey;
            skipElement();
            ++skipCount;
            continue;
          }
        }
        ++dropCount;
        if (Logger.IsEnabled(ZoneTreeLogLevel.Trace))
          Logger.LogTrace(GetPartRewriteMessage(part.SegmentId, dropCount, skipCount));

      }

      // Deleted records kept for older segments are not filtered.
      // prevKey is already set, so older duplicates of a dropped key are skipped too.
      if (mergeFilter != null &&
          !IsDeleted(minEntry.Key, value) &&
          !mergeFilter.Keep(minEntry.Key, value))
      {
        skipElement();
        continue;
      }

      diskSegmentCreator.Append(minEntry.Key, value, iteratorPosition);
      skipElement();
    }

    var newDiskSegment = diskSegmentCreator.CreateReadOnlyDiskSegment();
    try
    {
      // The replacement's names must survive before old bottom segments go away.
      Options.RandomAccessDeviceManager.SyncDirectory();
    }
    catch
    {
      newDiskSegment.Drop(diskSegmentCreator.AppendedPartSegmentIds);
      throw;
    }
    newDiskSegment.DropFailureReporter = ReportDropFailure;
    OnDiskSegmentCreated?.Invoke(this, newDiskSegment, true);
    lock (ShortMergerLock)
    {
      bottomSegments = BottomSegmentQueue.ToLastInFirstArray();
      var bottomSegmentsLength = bottomSegments.Length;
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
          Logger.LogError("Dropping the new disk segment of a bottom segments merge whose segments changed meanwhile failed. Its files can stay on disk.", e);
          OnCannotDropDiskSegment?.Invoke(newDiskSegment, e);
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
      var newQueue = new CopyOnWriteQueue<IDiskSegment<TKey, TValue>>(queue);
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
          Logger.LogError("Dropping a merged bottom segment failed. Its files can stay on disk.", e);
          OnCannotDropDiskSegment?.Invoke(diskSegmentToDrop, e);
        }
      }
    }

    TotalBottomSegmentsMergeSkipCount += skipCount;
    TotalBottomSegmentsMergeDropCount += dropCount;
    if (Logger.IsEnabled(ZoneTreeLogLevel.Trace))
      Logger.LogTrace(GetMergeSummary(
          dropCount, skipCount, stopwatch.ElapsedMilliseconds,
          TotalBottomSegmentsMergeDropCount, TotalBottomSegmentsMergeSkipCount));

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
      IDiskSegment<TKey, TValue>[] segments,
      long[] segmentIds)
  {
    if (segmentIds.Length == 0 || segmentIds.Length > segments.Length)
      return -1;

    var lastStartIndex = segments.Length - segmentIds.Length;
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
