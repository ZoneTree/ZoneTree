using System.Diagnostics;
using ZoneTree.Collections;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Segments.Disk;
using ZoneTree.Segments.InMemory;
using ZoneTree.Segments.MultiPart;
using ZoneTree.Segments.NullDisk;

namespace ZoneTree.Core;

public sealed partial class ZoneTree<TKey, TValue> : IZoneTree<TKey, TValue>, IZoneTreeMaintenance<TKey, TValue>
{
  /// <summary>
  /// Moves mutable segment into readonly segment.
  /// This will clear the writable region of the LSM tree.
  /// This method is thread-safe and can be called from many threads.
  /// The movement only occurs if the current mutable segment
  /// is the mutable segment passed by argument.
  /// </summary>
  /// <param name="mutableSegment">The mutable segment to move forward.</param>
  void MoveMutableSegmentForward(IMutableSegment<TKey, TValue> mutableSegment)
  {
    lock (AtomicUpdateLock)
    {
      // move segment zero only if
      // the given mutable segment is the current mutable segment (not already moved)
      // and it is not frozen.
      if (mutableSegment.IsFrozen || !ReferenceEquals(mutableSegment, MutableSegment))
        return;

      //Don't move empty mutable segment.
      var c = mutableSegment.Length;
      if (c == 0)
        return;

      MutableSegment = new FrozenMutableSegment<TKey, TValue>(mutableSegment);
      mutableSegment.Freeze();
      while (!mutableSegment.IsFullyFrozen) Thread.Yield();
      ReadOnlySegmentQueue.Enqueue(mutableSegment);
      MetaWal.EnqueueMaximumOpIndex(mutableSegment.MaximumOpIndex);
      MetaWal.EnqueueReadOnlySegment(mutableSegment.SegmentId);
      MutableSegment = new MutableSegment<TKey, TValue>(
          Options, IncrementalIdProvider.NextId(),
          mutableSegment.OpIndexProvider);
      MetaWal.NewMutableSegment(MutableSegment.SegmentId);
    }
    OnMutableSegmentMovedForward?.Invoke(this);
  }

  public void MoveMutableSegmentForward()
  {
    lock (AtomicUpdateLock)
    {
      MoveMutableSegmentForward(MutableSegment);
    }
  }

  public Thread StartMergeOperation()
  {
    if (IsMerging)
    {
      OnMergeOperationEnded?.Invoke(this, MergeResult.ANOTHER_MERGE_IS_RUNNING);
      return null;
    }

    OnMergeOperationStarted?.Invoke(this);
    var thread = new Thread(StartMergeOperationInternal);
    thread.Start();
    return thread;
  }

  void StartMergeOperationInternal()
  {
    if (IsMerging)
    {
      OnMergeOperationEnded?.Invoke(this, MergeResult.ANOTHER_MERGE_IS_RUNNING);
      return;
    }
    IsCancelMergeRequested = false;
    lock (LongMergerLock)
    {
      try
      {
        if (IsMerging)
        {
          OnMergeOperationEnded?.Invoke(this, MergeResult.ANOTHER_MERGE_IS_RUNNING);
          return;
        }
        IsMerging = true;
        var mergeResult = MergeReadOnlySegmentsInternal();
        IsMerging = false;
        OnMergeOperationEnded?.Invoke(this, mergeResult);

      }
      catch (Exception e)
      {
        Logger.LogError(e);
        OnMergeOperationEnded?.Invoke(this, MergeResult.FAILURE);
      }
      finally
      {
        IsMerging = false;
      }
    }
  }

  public void TryCancelMergeOperation()
  {
    IsCancelMergeRequested = true;
  }

  readonly int ReadOnlySegmentFullyFrozenSpinTimeout = 2000;

  MergeResult MergeReadOnlySegmentsInternal()
  {
    var stopwatch = new Stopwatch();
    stopwatch.Start();
    Logger.LogTrace("Merge starting.");

    var oldDiskSegment = DiskSegment;
    var roSegments = ReadOnlySegmentQueue.ToLastInFirstArray();

    if (roSegments.Any(x => !x.IsFullyFrozen))
    {
      SpinWait.SpinUntil(() => !roSegments.Any(x => !x.IsFullyFrozen),
          ReadOnlySegmentFullyFrozenSpinTimeout);
      if (roSegments.Any(x => !x.IsFullyFrozen))
        return MergeResult.RETRY_READONLY_SEGMENTS_ARE_NOT_READY;
    }

    Logger.LogTrace("Merge started.");

    var hasBottomSegments = !BottomSegmentQueue.IsEmpty;
    var readOnlySegmentsArray = roSegments.Select(x => x.GetSeekableIterator()).ToArray();
    if (readOnlySegmentsArray.Length == 0)
      return MergeResult.NOTHING_TO_MERGE;

    var mergingSegments = new List<ISeekableIterator<TKey, TValue>>();
    mergingSegments.AddRange(readOnlySegmentsArray);
    var hasDiskSegment = oldDiskSegment is not NullDiskSegment<TKey, TValue>;
    if (hasDiskSegment)
      mergingSegments.Add(oldDiskSegment.GetSeekableIterator());

    if (IsCancelMergeRequested)
    {
      readOnlySegmentsArray = null;
      mergingSegments = null;
      roSegments = null;
      oldDiskSegment = null;
      return MergeResult.CANCELLED_BY_USER;
    }

    var enableMultiPartDiskSegment =
        Options.DiskSegmentOptions.DiskSegmentMode == DiskSegmentMode.MultiPartDiskSegment;

    var len = mergingSegments.Count;
    var diskSegmentIndex = hasDiskSegment ? len - 1 : -1;

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

    var lastKeysOfEveryPart = hasDiskSegment ?
        oldDiskSegment.GetLastKeysOfEveryPart() :
        Array.Empty<TKey>();
    var lastValuesOfEveryPart = hasDiskSegment ?
        oldDiskSegment.GetLastValuesOfEveryPart() :
        Array.Empty<TValue>();
    var diskSegmentMinimumRecordCount = Options.DiskSegmentOptions.MinimumRecordCount;
    var isRangeDeleted = Options.IsRangeDeleted;

    var dropCount = 0;
    var skipCount = 0;
    while (heap.Count > 0)
    {
      if (IsCancelMergeRequested)
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
        readOnlySegmentsArray = null;
        mergingSegments = null;
        roSegments = null;
        oldDiskSegment = null;
        heap = null;
        return MergeResult.CANCELLED_BY_USER;
      }

      var minEntry = heap.MinValue;
      minSegmentIndex = minEntry.SegmentIndex;

      // Drop deleted parts before deletion/duplicate handling consumes their first record.
      if (!hasBottomSegments && isRangeDeleted != null &&
          minSegmentIndex == diskSegmentIndex &&
          mergingSegments[diskSegmentIndex].IsBeginningOfAPart)
      {
        var partIndex = mergingSegments[diskSegmentIndex].GetPartIndex();
        if (isRangeDeleted(minEntry.Key, minEntry.Value,
            lastKeysOfEveryPart[partIndex], lastValuesOfEveryPart[partIndex]))
        {
          mergingSegments[diskSegmentIndex].Skip(oldDiskSegment.GetPart(partIndex).Length - 1);
          skipElement();
          continue;
        }
      }

      // ignore deleted entries if bottom segments queue is empty.
      if (!hasBottomSegments && IsDeleted(minEntry.Key, minEntry.Value))
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
      var isDiskSegmentKey = minSegmentIndex == diskSegmentIndex;
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
        var part = oldDiskSegment
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
            mergingSegments[diskSegmentIndex].Skip(part.Length - 2);
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
    OnDiskSegmentCreated?.Invoke(this, newDiskSegment, false);
    lock (ShortMergerLock)
    {
      if (newDiskSegment.Length > Options.DiskSegmentMaxItemCount)
      {
        BottomSegmentQueue.Enqueue(newDiskSegment);
        MetaWal.EnqueueBottomSegment(newDiskSegment.SegmentId);
        MetaWal.NewDiskSegment(0);
        DiskSegment = new NullDiskSegment<TKey, TValue>();
      }
      else
      {
        DiskSegment = newDiskSegment;
        MetaWal.NewDiskSegment(newDiskSegment.SegmentId);
      }
      try
      {
        oldDiskSegment.Drop(diskSegmentCreator.AppendedPartSegmentIds);
      }
      catch (Exception e)
      {
        Logger.LogError(e);
        OnCanNotDropDiskSegment?.Invoke(oldDiskSegment, e);
      }

      len = readOnlySegmentsArray.Length;
      while (len > 0)
      {
        ReadOnlySegmentQueue.TryDequeue(out var segment);
        MetaWal.DequeueReadOnlySegment(segment.SegmentId);
        try
        {
          segment.Drop();
        }
        catch (Exception e)
        {
          Logger.LogError(e);
          OnCanNotDropReadOnlySegment?.Invoke(segment, e);
        }
        --len;
      }
    }

    TotalSkipCount += skipCount;
    TotalDropCount += dropCount;
    Logger.LogTrace(
        new LogMergerSuccess(
            dropCount,
            skipCount,
            stopwatch.ElapsedMilliseconds,
            TotalDropCount,
            TotalSkipCount));

    // Do not remove null assignments below and anywhere in this function!
    // GC does not collect local variables,
    // when this method is called by another thread.

    readOnlySegmentsArray = null;
    mergingSegments = null;
    roSegments = null;
    oldDiskSegment = null;
    heap = null;
    OnDiskSegmentActivated?.Invoke(this, newDiskSegment, false);
    newDiskSegment = null;
    return MergeResult.SUCCESS;
  }

  int TotalSkipCount;
  int TotalDropCount;
}
