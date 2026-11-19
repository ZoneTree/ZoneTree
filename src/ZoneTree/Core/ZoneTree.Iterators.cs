using ZoneTree.Collections;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Segments.NullDisk;

namespace ZoneTree.Core;

public sealed partial class ZoneTree<TKey, TValue> : IZoneTree<TKey, TValue>, IZoneTreeMaintenance<TKey, TValue>
{
  public SegmentCollection CollectSegments(
      bool includeMutableSegment,
      bool includeDiskSegment,
      bool includeBottomSegments,
      bool contributeToTheBlockCache,
      int diskSegmentPrefetchSize)
  {
    // The segments are read newest first and without the tree locks, as TryGet
    // reads them. Every iterator creation would otherwise queue on the locks.
    // The order is safe because every change publishes its result before it
    // removes its inputs: a moved mutable segment is queued before the next one
    // is published, a merge publishes its disk segment before it dequeues the
    // merged segments and retires the old disk segment, and a bottom merge
    // publishes the new queue before it retires the merged segments. So no
    // record is missed. A record can be read twice, from a segment and from the
    // merge that holds it, and the newer segment wins in the iterator's heap.
    // Segments stay readable after they move forward or merge.
    IMutableSegment<TKey, TValue> mutableSegment;
    List<IReadOnlySegment<TKey, TValue>> roSegments;
    IDiskSegment<TKey, TValue> diskSegment;
    List<IDiskSegment<TKey, TValue>> bottomSegments;
    while (!TryCollectSegments(
        includeMutableSegment,
        includeDiskSegment,
        includeBottomSegments,
        out mutableSegment,
        out roSegments,
        out diskSegment,
        out bottomSegments))
    {
      // A disk segment refused the iterator because its retirement has started.
      // Its records are in a segment published before that, so read again.
    }

    try
    {
      var seekableIterators = new List<ISeekableIterator<TKey, TValue>>(
          (mutableSegment == null ? 0 : 1) +
          roSegments.Count +
          (diskSegment == null ? 0 : 1) +
          (bottomSegments?.Count ?? 0));
      if (mutableSegment != null)
        seekableIterators.Add(mutableSegment.GetSeekableIterator());
      foreach (var roSegment in roSegments)
        seekableIterators.Add(roSegment.GetSeekableIterator());
      if (diskSegment != null)
        seekableIterators.Add(diskSegment.GetSeekableIterator(
            contributeToTheBlockCache,
            diskSegmentPrefetchSize));
      if (bottomSegments != null)
      {
        foreach (var bottom in bottomSegments)
          seekableIterators.Add(bottom.GetSeekableIterator(
              contributeToTheBlockCache,
              diskSegmentPrefetchSize));
      }
      return new SegmentCollection
      {
        SeekableIterators = seekableIterators,
        DiskSegment = diskSegment,
        BottomSegments = bottomSegments
      };
    }
    catch
    {
      // The caller only takes over the leases with the returned collection.
      // Release them here so the segments can still finish retiring.
      diskSegment?.DetachIterator();
      if (bottomSegments != null)
      {
        foreach (var bottom in bottomSegments)
          bottom.DetachIterator();
      }
      throw;
    }
  }

  /// <summary>
  /// Reads the segments newest first and attaches the disk segments. Returns false, with nothing attached, if a merge
  /// finished during the read or a disk segment's retirement started before it could be attached.
  /// </summary>
  /// <exception cref="InvalidOperationException">
  /// A retired segment is still published, which happens only when the tree has been dropped.
  /// </exception>
  bool TryCollectSegments(
      bool includeMutableSegment,
      bool includeDiskSegment,
      bool includeBottomSegments,
      out IMutableSegment<TKey, TValue> mutableSegment,
      out List<IReadOnlySegment<TKey, TValue>> roSegments,
      out IDiskSegment<TKey, TValue> diskSegment,
      out List<IDiskSegment<TKey, TValue>> bottomSegments)
  {
    // Every merge publishes a new disk segment, or a new NullDiskSegment when it moves its result to the bottom,
    // before it dequeues what it merged. The same disk segment before and after reading the in-memory segments
    // means no merge finished in between, so every read-only segment read is newer than everything on disk.
    // Otherwise a read-only segment read before the merge could hold an older value than the merged disk segment
    // and still win in the iterator's heap.
    var publishedDiskSegment = DiskSegment;
    mutableSegment = includeMutableSegment ? MutableSegment : null;
    roSegments = ReadOnlySegmentQueue.ToLastInFirstArray();
    diskSegment = null;
    bottomSegments = null;
    if (!ReferenceEquals(DiskSegment, publishedDiskSegment))
      return false;
    if (includeDiskSegment && publishedDiskSegment is not NullDiskSegment<TKey, TValue>)
    {
      if (!publishedDiskSegment.TryAttachIterator())
      {
        // A merge publishes the next disk segment before it retires this one, so a retired segment that is still
        // published was dropped with the tree. Attaching to it throws, as it did when the tree locks were held.
        if (!ReferenceEquals(DiskSegment, publishedDiskSegment))
          return false;
        publishedDiskSegment.AttachIterator();
      }
      diskSegment = publishedDiskSegment;
    }
    if (!includeBottomSegments)
      return true;

    var bottomQueue = BottomSegmentQueue;
    var bottoms = bottomQueue.ToLastInFirstArray();
    for (var i = 0; i < bottoms.Count; ++i)
    {
      if (bottoms[i].TryAttachIterator())
        continue;
      try
      {
        // A bottom merge publishes a new queue before it retires the merged segments, so a retired segment in the
        // queue that is still published was dropped with the tree, and attaching to it throws.
        if (ReferenceEquals(BottomSegmentQueue, bottomQueue))
        {
          bottoms[i].AttachIterator();
          continue;
        }
      }
      catch
      {
        DetachIterators(diskSegment, bottoms, i);
        throw;
      }
      DetachIterators(diskSegment, bottoms, i);
      diskSegment = null;
      return false;
    }
    bottomSegments = bottoms;
    return true;
  }

  static void DetachIterators(
      IDiskSegment<TKey, TValue> diskSegment,
      List<IDiskSegment<TKey, TValue>> bottoms,
      int attachedBottomCount)
  {
    for (var i = 0; i < attachedBottomCount; ++i)
      bottoms[i].DetachIterator();
    diskSegment?.DetachIterator();
  }

  /// <summary>
  /// Collects the immutable disk segment set and an in-memory-only iterator for
  /// one live backup generation. Disk segments are attached for physical file
  /// copy. The iterator excludes disk and bottom segments so in-memory records
  /// can be streamed without scanning immutable disk data again.
  /// </summary>
  public BackupSegmentCollection CollectBackupSegments(
      bool moveMutableSegmentForward,
      bool includeInMemorySegments)
  {
    if (moveMutableSegmentForward && includeInMemorySegments)
      MoveMutableSegmentForward(MutableSegment);

    BackupSegmentCollection result = null;
    try
    {
      lock (ShortMergerLock)
        lock (AtomicUpdateLock)
        {
          IDiskSegment<TKey, TValue> attachedDiskSegment = null;
          var attachedBottomSegments = new List<IDiskSegment<TKey, TValue>>();
          ZoneTreeIterator<TKey, TValue> inMemoryIterator = null;
          var completed = false;
          try
          {
            result = new BackupSegmentCollection();

            var diskSegment = DiskSegment;
            if (diskSegment is not NullDiskSegment<TKey, TValue>)
            {
              diskSegment.AttachIterator();
              attachedDiskSegment = diskSegment;
              result.DiskSegment = diskSegment;
            }

            var bottomSegments = BottomSegmentQueue.ToLastInFirstArray();
            foreach (var bottom in bottomSegments)
            {
              bottom.AttachIterator();
              attachedBottomSegments.Add(bottom);
            }
            result.BottomSegments = bottomSegments;

            if (includeInMemorySegments)
            {
              inMemoryIterator = moveMutableSegmentForward
                  ? (ZoneTreeIterator<TKey, TValue>)CreateReadOnlySegmentsIterator(
                      autoRefresh: false,
                      includeDeletedRecords: true)
                  : (ZoneTreeIterator<TKey, TValue>)CreateInMemorySegmentsIterator(
                      autoRefresh: false,
                      includeDeletedRecords: true);
              inMemoryIterator.Refresh();
            }

            result.InMemoryIterator = inMemoryIterator;
            completed = true;
          }
          finally
          {
            if (!completed)
            {
              inMemoryIterator?.Dispose();
              attachedDiskSegment?.DetachIterator();
              foreach (var bottom in attachedBottomSegments)
                bottom.DetachIterator();
              result = null;
            }
          }
        }

      if (moveMutableSegmentForward && includeInMemorySegments)
      {
        var iterator = (ZoneTreeIterator<TKey, TValue>)result.InMemoryIterator;
        iterator.WaitUntilReadOnlySegmentsBecomeFullyFrozen();
      }
      return result;
    }
    catch
    {
      result?.Dispose();
      throw;
    }
  }

  public sealed class SegmentCollection
  {
    public IReadOnlyList<ISeekableIterator<TKey, TValue>> SeekableIterators { get; set; }

    public IDiskSegment<TKey, TValue> DiskSegment { get; set; }

    public IReadOnlyList<IDiskSegment<TKey, TValue>> BottomSegments { get; set; }
  }

  public sealed class DiskSegmentCollection
  {
    public IDiskSegment<TKey, TValue> DiskSegment { get; set; }

    public IReadOnlyList<IDiskSegment<TKey, TValue>> BottomSegments { get; set; }
  }

  public sealed class BackupSegmentCollection : IDisposable
  {
    internal Backup.LiveBackupTreeKind TreeKind { get; set; }

    internal IReadOnlyList<KeyValuePair<TKey, TValue>> AdditionalRecords { get; set; } = [];

    bool IsDisposed;

    public IDiskSegment<TKey, TValue> DiskSegment { get; set; }

    public IReadOnlyList<IDiskSegment<TKey, TValue>> BottomSegments { get; set; }

    public IZoneTreeIterator<TKey, TValue> InMemoryIterator { get; set; }

    public void Dispose()
    {
      if (IsDisposed)
        return;

      InMemoryIterator?.Dispose();
      InMemoryIterator = null;

      DiskSegment?.DetachIterator();
      DiskSegment = null;

      if (BottomSegments != null)
      {
        foreach (var bottom in BottomSegments)
          bottom?.DetachIterator();
        BottomSegments = null;
      }

      IsDisposed = true;
    }
  }

  public IZoneTreeIterator<TKey, TValue> CreateIterator(
      IteratorType iteratorType, bool includeDeletedRecords, bool contributeToTheBlockCache)
  {
    return CreateIterator(
        iteratorType,
        includeDeletedRecords,
        contributeToTheBlockCache,
        IteratorDefaultValues.DiskSegmentPrefetchSize);
  }

  public IZoneTreeIterator<TKey, TValue> CreateIterator(
      IteratorType iteratorType,
      bool includeDeletedRecords,
      bool contributeToTheBlockCache,
      int diskSegmentPrefetchSize)
  {
    return CreateIterator(new IteratorOptions
    {
      IteratorType = iteratorType,
      IncludeDeletedRecords = includeDeletedRecords,
      ContributeToTheBlockCache = contributeToTheBlockCache,
      DiskSegmentPrefetchSize = diskSegmentPrefetchSize
    });
  }

  public IZoneTreeIterator<TKey, TValue> CreateIterator(IteratorOptions iteratorOptions)
  {
    ArgumentNullException.ThrowIfNull(iteratorOptions);
    var iteratorType = iteratorOptions.IteratorType;
    var includeMutableSegment = iteratorType is not IteratorType.Snapshot and
        not IteratorType.ReadOnlyRegion;

    var iterator = new ZoneTreeIterator<TKey, TValue>(
        Options,
        this,
        MinHeapEntryComparer,
        autoRefresh: iteratorType == IteratorType.AutoRefresh,
        isReverseIterator: false,
        iteratorOptions.IncludeDeletedRecords,
        includeMutableSegment: includeMutableSegment,
        includeDiskSegment: true,
        includeBottomSegments: true,
        iteratorOptions.DiskSegmentPrefetchSize);
    iterator.ContributeToTheBlockCache = iteratorOptions.ContributeToTheBlockCache;

    if (iteratorType == IteratorType.Snapshot)
    {
      MoveMutableSegmentForward(MutableSegment);
      iterator.Refresh();
      iterator.WaitUntilReadOnlySegmentsBecomeFullyFrozen();
    }
    else if (iteratorType == IteratorType.ReadOnlyRegion)
    {
      iterator.Refresh();
      iterator.WaitUntilReadOnlySegmentsBecomeFullyFrozen();
    }
    return iterator;
  }

  public IZoneTreeIterator<TKey, TValue> CreateReverseIterator(
      IteratorType iteratorType, bool includeDeletedRecords, bool contributeToTheBlockCache)
  {
    return CreateReverseIterator(
        iteratorType,
        includeDeletedRecords,
        contributeToTheBlockCache,
        IteratorDefaultValues.DiskSegmentPrefetchSize);
  }

  public IZoneTreeIterator<TKey, TValue> CreateReverseIterator(
      IteratorType iteratorType,
      bool includeDeletedRecords,
      bool contributeToTheBlockCache,
      int diskSegmentPrefetchSize)
  {
    return CreateReverseIterator(new IteratorOptions
    {
      IteratorType = iteratorType,
      IncludeDeletedRecords = includeDeletedRecords,
      ContributeToTheBlockCache = contributeToTheBlockCache,
      DiskSegmentPrefetchSize = diskSegmentPrefetchSize
    });
  }

  public IZoneTreeIterator<TKey, TValue> CreateReverseIterator(IteratorOptions iteratorOptions)
  {
    ArgumentNullException.ThrowIfNull(iteratorOptions);
    var iteratorType = iteratorOptions.IteratorType;
    var includeMutableSegment = iteratorType is not IteratorType.Snapshot and
        not IteratorType.ReadOnlyRegion;

    var iterator = new ZoneTreeIterator<TKey, TValue>(
        Options,
        this,
        MaxHeapEntryComparer,
        autoRefresh: iteratorType == IteratorType.AutoRefresh,
        isReverseIterator: true,
        iteratorOptions.IncludeDeletedRecords,
        includeMutableSegment: includeMutableSegment,
        includeDiskSegment: true,
        includeBottomSegments: true,
        iteratorOptions.DiskSegmentPrefetchSize);

    iterator.ContributeToTheBlockCache = iteratorOptions.ContributeToTheBlockCache;

    if (iteratorType == IteratorType.Snapshot)
    {
      MoveMutableSegmentForward(MutableSegment);
      iterator.Refresh();
      iterator.WaitUntilReadOnlySegmentsBecomeFullyFrozen();
    }
    else if (iteratorType == IteratorType.ReadOnlyRegion)
    {
      iterator.Refresh();
      iterator.WaitUntilReadOnlySegmentsBecomeFullyFrozen();
    }

    return iterator;
  }

  /// <summary>
  /// Creates an iterator that enables scanning of the readonly segments.
  /// </summary>
  /// <returns>ZoneTree Iterator</returns>
  public IZoneTreeIterator<TKey, TValue> CreateReadOnlySegmentsIterator(bool autoRefresh, bool includeDeletedRecords)
  {
    var iterator = new ZoneTreeIterator<TKey, TValue>(
        Options,
        this,
        MinHeapEntryComparer,
        autoRefresh: autoRefresh,
        isReverseIterator: false,
        includeDeletedRecords,
        includeMutableSegment: false,
        includeDiskSegment: false,
        includeBottomSegments: false,
        diskSegmentPrefetchSize: 0);
    return iterator;
  }

  /// <summary>
  /// Creates an iterator that enables scanning of the in-memory segments.
  /// This includes read-only segments and mutable segment.
  /// </summary>
  /// <param name="autoRefresh">if true, auto refresh is enabled.</param>
  /// <param name="includeDeletedRecords">if true the deleted records are included in iteration.</param>
  /// <returns>ZoneTree Iterator</returns>
  public IZoneTreeIterator<TKey, TValue>
      CreateInMemorySegmentsIterator(bool autoRefresh, bool includeDeletedRecords)
  {
    var iterator = new ZoneTreeIterator<TKey, TValue>(
        Options,
        this,
        MinHeapEntryComparer,
        autoRefresh: autoRefresh,
        isReverseIterator: false,
        includeDeletedRecords,
        includeMutableSegment: true,
        includeDiskSegment: false,
        includeBottomSegments: false,
        diskSegmentPrefetchSize: 0);
    return iterator;
  }
}
