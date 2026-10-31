using ZoneTree.Collections;
using ZoneTree.Collections.BTree;
using ZoneTree.Exceptions;
using ZoneTree.Hashers;
using ZoneTree.Segments;

namespace ZoneTree.Core;

public sealed partial class ZoneTree<TKey, TValue> : IZoneTree<TKey, TValue>, IZoneTreeMaintenance<TKey, TValue>
{
  public bool ContainsKey(in TKey key)
  {
    var keyHashProvider = new KeyHashProvider<TKey>();
    if (MutableSegment.TryGet(key, out TValue value, ref keyHashProvider))
      return !IsDeleted(key, value);

    return TryGetFromReadonlySegments(in key, out _, ref keyHashProvider);
  }

  bool TryGetFromReadonlySegments(
      in TKey key,
      out TValue value,
      ref KeyHashProvider<TKey> keyHashProvider)
  {
    // The queues are usually empty. Checking first avoids allocating an
    // enumerator on every lookup.
    if (!ReadOnlySegmentQueue.IsEmpty)
    {
      foreach (var segment in ReadOnlySegmentQueue)
      {
        if (segment.TryGet(key, out value, ref keyHashProvider))
        {
          return !IsDeleted(key, value);
        }
      }
    }

    while (true)
    {
      try
      {
        if (DiskSegment.TryGet(key, out value, ref keyHashProvider))
        {
          return !IsDeleted(key, value);
        }

        var bottomSegments = BottomSegmentQueue;
        if (bottomSegments.IsEmpty)
          return false;
        foreach (var segment in bottomSegments)
        {
          if (segment.TryGet(key, out value, ref keyHashProvider))
          {
            return !IsDeleted(key, value);
          }
        }
        return false;
      }
      catch (DiskSegmentIsDroppingException)
      {
        continue;
      }
    }
  }

  public void VisitSegments<TVisitor>(in TKey key, ref TVisitor visitor)
      where TVisitor : struct, ISegmentRecordVisitor<TKey, TValue>
  {
    // Segments are read in the order TryGet reads them, newest first and without
    // the tree locks. A segment that moves forward or merges during the visit can
    // be read twice, but no record is missed.
    if (MutableSegment.Visit(in key, ref visitor) == SegmentVisitAction.Stop)
      return;
    if (!ReadOnlySegmentQueue.IsEmpty)
    {
      foreach (var segment in ReadOnlySegmentQueue)
      {
        if (VisitSeekableSegment(segment.GetSeekableIterator(), in key, ref visitor) ==
            SegmentVisitAction.Stop)
          return;
      }
    }

    while (true)
    {
      try
      {
        if (VisitDiskSegment(DiskSegment, in key, ref visitor) == SegmentVisitAction.Stop)
          return;
        var bottomSegments = BottomSegmentQueue;
        if (bottomSegments.IsEmpty)
          return;
        foreach (var segment in bottomSegments)
        {
          if (VisitDiskSegment(segment, in key, ref visitor) == SegmentVisitAction.Stop)
            return;
        }
        return;
      }
      catch (DiskSegmentIsDroppingException)
      {
        // A merge replaced the segment. Its records are in the newer disk segments.
        continue;
      }
    }
  }

  static SegmentVisitAction VisitSeekableSegment<TVisitor>(
      ISeekableIterator<TKey, TValue> iterator,
      in TKey key,
      ref TVisitor visitor)
      where TVisitor : struct, ISegmentRecordVisitor<TKey, TValue>
  {
    if (!iterator.SeekToFirstGreaterOrEqualElement(in key))
      return SegmentVisitAction.NextSegment;
    do
    {
      var action = visitor.Visit(iterator.CurrentKey, iterator.CurrentValue);
      if (action != SegmentVisitAction.NextRecord)
        return action;
    } while (iterator.Next());
    return SegmentVisitAction.NextSegment;
  }

  static SegmentVisitAction VisitDiskSegment<TVisitor>(
      IDiskSegment<TKey, TValue> segment,
      in TKey key,
      ref TVisitor visitor)
      where TVisitor : struct, ISegmentRecordVisitor<TKey, TValue>
  {
    var length = segment.Length;
    if (length == 0)
      return SegmentVisitAction.NextSegment;
    for (var position = segment.GetFirstGreaterOrEqualPosition(in key);
        position < length;
        ++position)
    {
      var action = visitor.Visit(segment.GetKey(position), segment.GetValue(position));
      if (action != SegmentVisitAction.NextRecord)
        return action;
    }
    return SegmentVisitAction.NextSegment;
  }

  public bool TryGet(in TKey key, out TValue value)
  {
    var keyHashProvider = new KeyHashProvider<TKey>();
    if (MutableSegment.TryGet(key, out value, ref keyHashProvider))
    {
      return !IsDeleted(key, value);
    }
    return TryGetFromReadonlySegments(in key, out value, ref keyHashProvider);
  }

  public bool TryAdd(in TKey key, in TValue value, out long opIndex)
  {
    if (ContainsKey(key))
    {
      opIndex = 0;
      return false;
    }
    opIndex = Upsert(in key, in value);
    return true;
  }

  public bool TryGetAndUpdate(
      in TKey key,
      out TValue value,
      ValueUpdaterDelegate<TValue> valueUpdater,
      out long opIndex)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();

    var keyHashProvider = new KeyHashProvider<TKey>();
    if (MutableSegment.TryGet(key, out value, ref keyHashProvider))
    {
      if (IsDeleted(key, value))
      {
        opIndex = 0;
        return false;
      }
    }
    else if (!TryGetFromReadonlySegments(in key, out value, ref keyHashProvider))
    {
      opIndex = 0;
      return false;
    }

    if (!valueUpdater(ref value))
    {
      // return true because
      // no update happened, but the value is found.
      opIndex = 0;
      return true;
    }
    opIndex = Upsert(in key, in value);
    return true;
  }

  public bool TryAtomicGetAndUpdate(
      in TKey key,
      out TValue value,
      ValueUpdaterDelegate<TValue> valueUpdater)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();

    lock (AtomicUpdateLock)
    {
      var keyHashProvider = new KeyHashProvider<TKey>();
      if (MutableSegment.TryGet(key, out value, ref keyHashProvider))
      {
        if (IsDeleted(key, value))
          return false;
      }
      else if (!TryGetFromReadonlySegments(in key, out value, ref keyHashProvider))
        return false;

      if (!valueUpdater(ref value))
      {
        // return true because
        // no update happened, but the value is found.
        return true;
      }

      Upsert(in key, in value);
      return true;
    }
  }

  public bool TryAtomicAdd(in TKey key, in TValue value, out long opIndex)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();

    lock (AtomicUpdateLock)
    {
      if (ContainsKey(key))
      {
        opIndex = 0;
        return false;
      }
      opIndex = Upsert(key, value);
      return true;
    }
  }

  public bool TryAtomicUpdate(in TKey key, in TValue value, out long opIndex)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();

    lock (AtomicUpdateLock)
    {
      if (!ContainsKey(key))
      {
        opIndex = 0;
        return false;
      }
      opIndex = Upsert(key, value);
      return true;
    }
  }

  static OperationResultDelegate<TValue> EmptyOperationResultDelegate =
      (in TValue value, long opIndex, OperationResult result) =>
      {
      };

  public bool TryAtomicAddOrUpdate(
      in TKey key,
      in TValue valueToAdd,
      ValueUpdaterDelegate<TValue> valueUpdater,
      OperationResultDelegate<TValue> result)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    AddOrUpdateResult status;
    IMutableSegment<TKey, TValue> mutableSegment;
    var opIndex = 0L;
    result ??= EmptyOperationResultDelegate;
    var keyHashProvider = new KeyHashProvider<TKey>();

    while (true)
    {
      lock (AtomicUpdateLock)
      {
        mutableSegment = MutableSegment;
        if (mutableSegment.IsFrozen)
        {
          status = AddOrUpdateResult.RETRY_SEGMENT_IS_FULL;
        }
        else if (mutableSegment.TryGet(in key, out var existing, ref keyHashProvider))
        {
          if (!valueUpdater(ref existing))
          {
            result(in existing, 0, OperationResult.Cancelled);
            return false;
          }
          status = mutableSegment.Upsert(key, existing, out opIndex);
          if (status == AddOrUpdateResult.UPDATED)
          {
            result(in existing, opIndex, OperationResult.Updated);
            return false;
          }
        }
        else if (TryGetFromReadonlySegments(in key, out existing, ref keyHashProvider))
        {
          if (!valueUpdater(ref existing))
          {
            result(in existing, 0, OperationResult.Cancelled);
            return false;
          }
          status = mutableSegment.Upsert(key, existing, out opIndex);
          if (status == AddOrUpdateResult.ADDED)
          {
            result(in existing, opIndex, OperationResult.Updated);
            return false;
          }
        }
        else
        {
          status = mutableSegment.Upsert(key, valueToAdd, out opIndex);
          if (status == AddOrUpdateResult.ADDED)
          {
            result(in valueToAdd, opIndex, OperationResult.Added);
            return true;
          }
        }
      }
      switch (status)
      {
        case AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN:
          Thread.Yield();
          continue;
        case AddOrUpdateResult.RETRY_SEGMENT_IS_FULL:
          MoveMutableSegmentForward(mutableSegment);
          continue;
        default:
          throw new Exception("Impossible.");
      }
    }
  }

  public bool TryAtomicAddOrUpdate(
      in TKey key,
      ValueAdderDelegate<TValue> valueAdder,
      ValueUpdaterDelegate<TValue> valueUpdater,
      OperationResultDelegate<TValue> result)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    AddOrUpdateResult status;
    IMutableSegment<TKey, TValue> mutableSegment;
    var opIndex = 0L;
    result ??= EmptyOperationResultDelegate;
    var keyHashProvider = new KeyHashProvider<TKey>();
    while (true)
    {
      lock (AtomicUpdateLock)
      {
        mutableSegment = MutableSegment;
        if (mutableSegment.IsFrozen)
        {
          status = AddOrUpdateResult.RETRY_SEGMENT_IS_FULL;
        }
        else if (mutableSegment.TryGet(in key, out var existing, ref keyHashProvider))
        {
          if (!valueUpdater(ref existing))
          {
            result(in existing, 0, OperationResult.Cancelled);
            return false;
          }
          status = mutableSegment.Upsert(key, existing, out opIndex);
          if (status == AddOrUpdateResult.UPDATED)
          {
            result(in existing, opIndex, OperationResult.Updated);
            return false;
          }
        }
        else if (TryGetFromReadonlySegments(in key, out existing, ref keyHashProvider))
        {
          if (!valueUpdater(ref existing))
          {
            result(in existing, 0, OperationResult.Cancelled);
            return false;
          }
          status = mutableSegment.Upsert(key, existing, out opIndex);
          if (status == AddOrUpdateResult.ADDED)
          {
            result(in existing, opIndex, OperationResult.Updated);
            return false;
          }
        }
        else
        {
          if (!valueAdder(ref existing))
          {
            result(in existing, 0, OperationResult.Cancelled);
            return false;
          }
          status = mutableSegment.Upsert(key, existing, out opIndex);
          if (status == AddOrUpdateResult.ADDED)
          {
            result(in existing, opIndex, OperationResult.Added);
            return true;
          }
        }
      }
      switch (status)
      {
        case AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN:
          Thread.Yield();
          continue;
        case AddOrUpdateResult.RETRY_SEGMENT_IS_FULL:
          MoveMutableSegmentForward(mutableSegment);
          continue;
        default:
          throw new Exception("Impossible.");
      }
    }
  }

  public long AtomicUpsert(in TKey key, in TValue value)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    lock (AtomicUpdateLock)
    {
      return Upsert(in key, in value);
    }
  }

  public long Upsert(in TKey key, in TValue value)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    while (true)
    {
      var mutableSegment = MutableSegment;
      var status = mutableSegment.Upsert(key, value, out var opIndex);
      switch (status)
      {
        case AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN:
          Thread.Yield();
          continue;
        case AddOrUpdateResult.RETRY_SEGMENT_IS_FULL:
          MoveMutableSegmentForward(mutableSegment);
          continue;
        default:
          return opIndex;
      }
    }
  }

  public long Upsert(in TKey key, GetValueDelegate<TKey, TValue> valueGetter)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    while (true)
    {
      var mutableSegment = MutableSegment;
      var status = mutableSegment.Upsert(key, valueGetter, out var opIndex);
      switch (status)
      {
        case AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN:
          Thread.Yield();
          continue;
        case AddOrUpdateResult.RETRY_SEGMENT_IS_FULL:
          MoveMutableSegmentForward(mutableSegment);
          continue;
        default:
          return opIndex;
      }
    }
  }

  public bool TryDelete(in TKey key, out long opIndex)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    if (!ContainsKey(key))
    {
      opIndex = 0;
      return false;
    }
    opIndex = ForceDelete(in key);
    return true;
  }

  public long ForceDelete(in TKey key)
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    long opIndex;
    while (true)
    {
      var mutableSegment = MutableSegment;
      var status = mutableSegment.Delete(key, out opIndex);
      switch (status)
      {
        case AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN:
          Thread.Yield();
          continue;
        case AddOrUpdateResult.RETRY_SEGMENT_IS_FULL:
          MoveMutableSegmentForward(mutableSegment);
          continue;
        default: return opIndex;
      }
    }
  }
}
