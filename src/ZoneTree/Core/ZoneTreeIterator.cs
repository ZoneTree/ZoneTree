using ZoneTree.Collections;
using ZoneTree.Comparers;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Segments;

namespace ZoneTree.Core;

public sealed class ZoneTreeIterator<TKey, TValue> : IZoneTreeIterator<TKey, TValue>
{
  readonly Lock SyncRoot = new();

  readonly ZoneTree<TKey, TValue> ZoneTree;

  readonly long RegistrationId;

  readonly IRefComparer<HeapEntry<TKey, TValue>> HeapEntryComparer;

  readonly bool IsReverseIterator;

  FixedSizeMinHeap<HeapEntry<TKey, TValue>> Heap;

  IReadOnlyList<ISeekableIterator<TKey, TValue>> SeekableIterators;

  ISeekableIterator<TKey, TValue> SingleIterator;

  bool SingleIteratorHasCurrent;

  readonly IsDeletedDelegate<TKey, TValue> IsDeleted;

  readonly IRefComparer<TKey> Comparer;

  readonly bool IncludeDeletedRecords;

  readonly bool IncludeMutableSegment;

  readonly bool IncludeDiskSegment;

  readonly bool IncludeBottomSegments;

  readonly int DiskSegmentPrefetchSize;

  int Length;

  bool HasPrev;

  TKey PrevKey;

  TKey currentKey;

  TValue currentValue;

  bool IsHeapFilled;

  volatile bool DoesRequireRefresh;

  public TKey CurrentKey
  {
    get =>
        HasCurrent ?
        currentKey :
        throw new ZoneTreeIteratorPositionException();
    private set => currentKey = value;
  }

  public TValue CurrentValue
  {
    get =>
        HasCurrent ?
        currentValue :
        throw new ZoneTreeIteratorPositionException();
    private set => currentValue = value;
  }

  public bool HasCurrent { get; private set; }

  public bool AutoRefresh { get; }

  public KeyValuePair<TKey, TValue> Current => KeyValuePair.Create(CurrentKey, CurrentValue);

  public IDiskSegment<TKey, TValue> DiskSegment { get; private set; }

  public IReadOnlyList<IDiskSegment<TKey, TValue>> BottomSegments { get; private set; }

  public bool ContributeToTheBlockCache { get; set; }

  public ZoneTreeIterator(
      ZoneTreeOptions<TKey, TValue> options,
      ZoneTree<TKey, TValue> zoneTree,
      IRefComparer<HeapEntry<TKey, TValue>> heapEntryComparer,
      bool autoRefresh,
      bool isReverseIterator,
      bool includeDeletedRecords,
      bool includeMutableSegment,
      bool includeDiskSegment,
      bool includeBottomSegments,
      int diskSegmentPrefetchSize)
  {
    IsDeleted = options.IsDeleted;
    Comparer = options.Comparer;
    ZoneTree = zoneTree;
    HeapEntryComparer = heapEntryComparer;
    AutoRefresh = autoRefresh;
    IsReverseIterator = isReverseIterator;
    IncludeDeletedRecords = includeDeletedRecords;
    IncludeMutableSegment = includeMutableSegment;
    IncludeDiskSegment = includeDiskSegment;
    DiskSegmentPrefetchSize = diskSegmentPrefetchSize;
    if (autoRefresh)
      ZoneTree.OnMutableSegmentMovedForward += OnZoneTreeMutableSegmentMovedForward;
    RegistrationId = ZoneTree.RegisterIterator(this);
    IncludeBottomSegments = includeBottomSegments;
  }

  internal void ReleaseOnTreeDisposing()
  {
    ReleaseResources();
  }

  void OnZoneTreeMutableSegmentMovedForward(IZoneTreeMaintenance<TKey, TValue> zoneTree)
  {
    DoesRequireRefresh = true;
  }

  public bool Next()
  {
    if (DoesRequireRefresh)
      Refresh();
    if (!IsHeapFilled)
      SeekFirst();
    if (SingleIterator != null)
      return NextSingleIterator();
    return IsReverseIterator ? PrevInternal() : NextInternal();
  }

  public void Seek(in TKey key)
  {
    if (DoesRequireRefresh || Heap == null)
    {
      ClearMarkers();
      Refresh();
    }
    SeekInternal(in key, true);
  }

  public void SeekFirst()
  {
    ClearMarkers();
    if (Heap == null)
    {
      Refresh();
    }
    Heap.Clear();
    IsHeapFilled = false;
    var len = Length;
    if (IsReverseIterator)
    {
      for (int i = 0; i < len; i++)
      {
        var s = SeekableIterators[i];
        if (!s.SeekEnd())
          continue;
        var key = s.CurrentKey;
        var value = s.CurrentValue;
        var entry = new HeapEntry<TKey, TValue>(key, value, i);
        Heap.Insert(entry);
      }
      SetHeapFilled();
      return;
    }
    for (int i = 0; i < len; i++)
    {
      var s = SeekableIterators[i];
      if (!s.SeekBegin())
        continue;
      var key = s.CurrentKey;
      var value = s.CurrentValue;
      var entry = new HeapEntry<TKey, TValue>(key, value, i);
      Heap.Insert(entry);
    }
    SetHeapFilled();
  }

  void SeekInternal(in TKey key, bool clearMarkers)
  {
    Heap.Clear();
    IsHeapFilled = false;
    if (clearMarkers)
      ClearMarkers();

    var len = Length;
    if (IsReverseIterator)
    {
      for (int i = 0; i < len; i++)
      {
        var s = SeekableIterators[i];
        if (!s.SeekToLastSmallerOrEqualElement(in key))
          continue;
        var entry = new HeapEntry<TKey, TValue>(s.CurrentKey, s.CurrentValue, i);
        Heap.Insert(entry);
      }
      SetHeapFilled();
      return;
    }
    for (int i = 0; i < len; i++)
    {
      var s = SeekableIterators[i];
      if (!s.SeekToFirstGreaterOrEqualElement(in key))
        continue;
      var entry = new HeapEntry<TKey, TValue>(s.CurrentKey, s.CurrentValue, i);
      Heap.Insert(entry);
    }
    SetHeapFilled();
  }

  void SetHeapFilled()
  {
    IsHeapFilled = true;
    SingleIterator = Heap.Count == 1
        ? SeekableIterators[Heap.MinValue.SegmentIndex]
        : null;
    SingleIteratorHasCurrent = SingleIterator != null;
  }

  bool NextSingleIterator()
  {
    // A settled tree commonly has only one nonempty segment. Its iterator
    // already produces ordered, distinct keys, so no heap update is needed.
    // Read the next entry only on demand, avoiding an unused prefetch at the
    // end of a limited scan.
    var iterator = SingleIterator;
    while (SingleIteratorHasCurrent)
    {
      var key = iterator.CurrentKey;
      var value = iterator.CurrentValue;
      SingleIteratorHasCurrent = IsReverseIterator ? iterator.Prev() : iterator.Next();
      if (!IncludeDeletedRecords && IsDeleted(key, value))
      {
        PrevKey = key;
        HasPrev = true;
        continue;
      }
      if (HasPrev && Comparer.Compare(in key, in PrevKey) == 0)
        continue;
      PrevKey = key;
      HasPrev = true;
      CurrentKey = key;
      CurrentValue = value;
      HasCurrent = true;
      return true;
    }
    HasCurrent = false;
    return false;
  }

  void ReleaseResources()
  {
    IsHeapFilled = false;
    Heap = null;
    SingleIterator = null;
    SingleIteratorHasCurrent = false;
    DetachFromDiskSegments();
    SeekableIterators = null;
  }

  void DetachFromDiskSegments()
  {
    lock (SyncRoot)
    {
      DiskSegment?.DetachIterator();
      DiskSegment = null;
      if (BottomSegments == null)
        return;
      var bos = BottomSegments;
      var len = bos.Count;
      for (var i = 0; i < len; ++i)
      {
        bos[i]?.DetachIterator();
      }
      BottomSegments = null;
    }
  }

  void ClearMarkers()
  {
    PrevKey = default;
    HasPrev = false;
    CurrentKey = default;
    CurrentValue = default;
    HasCurrent = false;
  }

  public void Refresh()
  {
    DoesRequireRefresh = false;
    IsHeapFilled = false;
    ReleaseResources();
    var segments = ZoneTree
        .CollectSegments(
            IncludeMutableSegment,
            IncludeDiskSegment,
            IncludeBottomSegments,
            ContributeToTheBlockCache,
            DiskSegmentPrefetchSize);
    DiskSegment = segments.DiskSegment;
    BottomSegments = segments.BottomSegments;
    SeekableIterators = segments.SeekableIterators;
    Length = SeekableIterators.Count;
    Heap = new FixedSizeMinHeap<HeapEntry<TKey, TValue>>(Length + 1, HeapEntryComparer);
    if (HasCurrent)
    {
      SeekInternal(in currentKey, false);
    }
  }

  bool PrevInternal()
  {
    int minSegmentIndex;
    void skipElement()
    {
      var minSegment = SeekableIterators[minSegmentIndex];
      if (minSegment.Prev())
      {
        var key = minSegment.CurrentKey;
        var value = minSegment.CurrentValue;
        Heap.ReplaceMin(new HeapEntry<TKey, TValue>(key, value, minSegmentIndex));
      }
      else
      {
        Heap.RemoveMin();
      }
    }

    while (Heap.Count > 0)
    {
      var minEntry = Heap.MinValue;
      minSegmentIndex = minEntry.SegmentIndex;

      // ignore deleted entries.
      if (!IncludeDeletedRecords && IsDeleted(minEntry.Key, minEntry.Value))
      {
        skipElement();
        PrevKey = minEntry.Key;
        HasPrev = true;
        continue;
      }

      if (HasPrev && Comparer.Compare(minEntry.Key, PrevKey) == 0)
      {
        skipElement();
        continue;
      }
      PrevKey = minEntry.Key;
      HasPrev = true;

      CurrentKey = minEntry.Key;
      CurrentValue = minEntry.Value;
      HasCurrent = true;
      skipElement();
      return true;
    }
    HasCurrent = false;
    return false;
  }

  bool NextInternal()
  {
    int minSegmentIndex;
    void skipElement()
    {
      var minSegment = SeekableIterators[minSegmentIndex];
      if (minSegment.Next())
      {
        var key = minSegment.CurrentKey;
        var value = minSegment.CurrentValue;
        Heap.ReplaceMin(new HeapEntry<TKey, TValue>(key, value, minSegmentIndex));
      }
      else
      {
        Heap.RemoveMin();
      }
    }
    ;

    while (Heap.Count > 0)
    {
      var minEntry = Heap.MinValue;
      minSegmentIndex = minEntry.SegmentIndex;

      // ignore deleted entries.
      if (!IncludeDeletedRecords && IsDeleted(minEntry.Key, minEntry.Value))
      {
        skipElement();
        PrevKey = minEntry.Key;
        HasPrev = true;
        continue;
      }

      if (HasPrev && Comparer.Compare(minEntry.Key, PrevKey) == 0)
      {
        skipElement();
        continue;
      }
      PrevKey = minEntry.Key;
      HasPrev = true;

      CurrentKey = minEntry.Key;
      CurrentValue = minEntry.Value;
      HasCurrent = true;
      skipElement();
      return true;
    }
    HasCurrent = false;
    return false;
  }

  public void Dispose()
  {
    if (AutoRefresh)
      ZoneTree.OnMutableSegmentMovedForward -= OnZoneTreeMutableSegmentMovedForward;
    ZoneTree.UnregisterIterator(RegistrationId);
    ReleaseResources();
  }

  public void WaitUntilReadOnlySegmentsBecomeFullyFrozen()
  {
    var iterators = SeekableIterators;
    var spinWait = new SpinWait();
    while (true)
    {
      if (iterators.Any(x => !x.IsFullyFrozen))
      {
        spinWait.SpinOnce();
        continue;
      }
      return;
    }
  }
}
