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
    // Capture a consistent segment set and attach the disk segments under the
    // locks, then create the iterators outside them. Iterator creation
    // allocates buffers and takes B+tree locks, and doing it under the tree
    // locks serializes every iterator creation in the process. Segments stay
    // readable after they move forward or merge, as they must for iterators
    // that are already open.
    IMutableSegment<TKey, TValue> mutableSegment = null;
    List<IReadOnlySegment<TKey, TValue>> roSegments;
    IDiskSegment<TKey, TValue> diskSegment = null;
    List<IDiskSegment<TKey, TValue>> bottomSegments = null;
    lock (ShortMergerLock)
      lock (AtomicUpdateLock)
      {
        if (includeMutableSegment)
          mutableSegment = MutableSegment;
        roSegments = ReadOnlySegmentQueue.ToLastInFirstArray();
        if (includeDiskSegment)
        {
          var segment = DiskSegment;
          if (segment is not NullDiskSegment<TKey, TValue>)
          {
            segment.AttachIterator();
            diskSegment = segment;
          }
        }
        if (includeBottomSegments)
        {
          bottomSegments = BottomSegmentQueue.ToLastInFirstArray();
          foreach (var bottom in bottomSegments)
            bottom.AttachIterator();
        }
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
      MoveMutableSegmentForward();

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
      MoveMutableSegmentForward();
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
      MoveMutableSegmentForward();
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
