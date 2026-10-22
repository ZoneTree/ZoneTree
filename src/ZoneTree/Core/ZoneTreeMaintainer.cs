using System.Collections.Concurrent;
using System.Threading;
using ZoneTree.Logger;
using ZoneTree.Segments;
using ZoneTree.Segments.Disk;

namespace ZoneTree.Core;

/// <summary>
/// The maintainer for ZoneTree to control merge operations and memory compaction.
/// </summary>
/// <remarks>
/// Disposal waits for tracked background merge threads to finish. Call
/// <see cref="TryCancelBackgroundThreads"/> before disposal to request
/// cancellation instead of waiting for the current work to complete normally.
/// </remarks>
/// <typeparam name="TKey">The key type</typeparam>
/// <typeparam name="TValue">The value type</typeparam>
public sealed class ZoneTreeMaintainer<TKey, TValue> : IMaintainer, IDisposable
{
  readonly Lock SyncRoot = new();

  readonly ILogger Logger;

  volatile bool RestartMerge;

  CancellationTokenSource PeriodicTimerCancellationTokenSource = new();

  readonly ConcurrentDictionary<int, Thread> MergerThreads = new();

  volatile bool isPeriodicTimerRunning;

  /// <summary>
  /// The associated ZoneTree instance.
  /// </summary>
  public IZoneTree<TKey, TValue> ZoneTree { get; }

  /// <summary>
  /// The associated ZoneTree maintenance instance.
  /// </summary>
  public IZoneTreeMaintenance<TKey, TValue> Maintenance { get; }

  /// <inheritdoc/>
  public int ThresholdForMergeOperationStart { get; set; }

  /// <inheritdoc/>
  public int MaximumReadOnlySegmentCount { get; set; } = 64;

  /// <inheritdoc/>
  public bool EnableJobForCleaningInactiveCaches
  {
    get => isPeriodicTimerRunning;
    set
    {
      if (value && !isPeriodicTimerRunning)
        Task.Run(StartPeriodicTimer);
      else if (!value)
        StopPeriodicTimer();
    }
  }

  /// <inheritdoc/>
  public TimeSpan BlockCacheLifeTime { get; set; } = TimeSpan.FromMinutes(1);

  /// <inheritdoc/>
  public TimeSpan InactiveBlockCacheCleanupInterval { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Creates a ZoneTreeMaintainer.
  /// </summary>
  /// <param name="zoneTree">The ZoneTree</param>
  /// <param name="startJobForCleaningInactiveBlockCaches">Starts periodic timer if true.</param>
  /// <param name="logger">The logger</param>
  public ZoneTreeMaintainer(IZoneTree<TKey, TValue> zoneTree,
      bool startJobForCleaningInactiveBlockCaches = true,
      ILogger logger = null)
  {
    Logger = logger ?? zoneTree.Logger;
    ZoneTree = zoneTree;
    Maintenance = zoneTree.Maintenance;
    AttachEvents();
    if (startJobForCleaningInactiveBlockCaches)
      Task.Run(StartPeriodicTimer);
  }

  /// <summary>
  /// Creates a ZoneTreeMaintainer.
  /// </summary>
  /// <param name="zoneTree">The transactional ZoneTree</param>
  /// <param name="logger">The logger</param>
  public ZoneTreeMaintainer(ITransactionalZoneTree<TKey, TValue> zoneTree, ILogger logger = null)
  {
    Logger = logger ?? zoneTree.Logger;
    ZoneTree = zoneTree.Maintenance.ZoneTree;
    Maintenance = ZoneTree.Maintenance;
    AttachEvents();
    Task.Run(StartPeriodicTimer);
  }

  void AttachEvents()
  {
    Maintenance.OnMutableSegmentMovedForward += OnMutableSegmentMovedForward;
    Maintenance.OnDiskSegmentCreated += OnDiskSegmentCreated;
    Maintenance.OnMergeOperationEnded += OnMergeOperationEnded;
    Maintenance.OnZoneTreeIsDisposing += OnZoneTreeIsDisposing;
    Maintenance.OnBottomSegmentsMergeOperationEnded += OnBottomSegmentsMergeOperationEnded;
  }


  void OnZoneTreeIsDisposing(IZoneTreeMaintenance<TKey, TValue> zoneTree)
  {
    Trace("ZoneTree is disposing. ZoneTreeMaintainer disposal started.");
    PeriodicTimerCancellationTokenSource.Cancel();
    WaitForBackgroundThreads();
    Dispose();
    Trace("ZoneTreeMaintainer is disposed.");
  }

  void OnBottomSegmentsMergeOperationEnded(
      IZoneTreeMaintenance<TKey, TValue> zoneTree,
      MergeResult mergeResult)
  {
    Trace(mergeResult.ToString());
    MergerThreads.Remove(Environment.CurrentManagedThreadId, out _);
  }

  void OnMergeOperationEnded(
      IZoneTreeMaintenance<TKey, TValue> zoneTree,
      MergeResult mergeResult)
  {
    switch (mergeResult)
    {
      case MergeResult.RETRY_READONLY_SEGMENTS_ARE_NOT_READY:
        Trace("RETRY_READONLY_SEGMENTS_ARE_NOT_READY");
        RestartMerge = false;
        StartMerge();
        break;
      case MergeResult.ANOTHER_MERGE_IS_RUNNING:
        Trace("ANOTHER_MERGE_IS_RUNNING");
        RestartMerge = true;
        break;
      case MergeResult.NOTHING_TO_MERGE:
        Trace("NOTHING_TO_MERGE");
        break;
      case MergeResult.CANCELLED_BY_USER:
        Trace("CANCELLED_BY_USER");
        break;
      case MergeResult.SUCCESS:
        Trace("MERGE SUCCESS");
        if (RestartMerge)
        {
          Trace("Restarting merge");
          RestartMerge = false;
          StartMerge();
        }
        break;
      case MergeResult.FAILURE:
        Trace("MERGE FAILURE");
        break;
    }
    MergerThreads.Remove(Environment.CurrentManagedThreadId, out _);
  }

  void OnDiskSegmentCreated(
      IZoneTreeMaintenance<TKey, TValue> zoneTree,
      IDiskSegment<TKey, TValue> newDiskSegment,
      bool isBottomSegment)
  {
  }

  void OnMutableSegmentMovedForward(IZoneTreeMaintenance<TKey, TValue> zoneTree)
  {
    if (Maintenance.ReadOnlySegmentsCount > MaximumReadOnlySegmentCount)
      StartMerge();
    else if (Maintenance.ReadOnlySegmentsRecordCount > ThresholdForMergeOperationStart)
      StartMerge();
  }

  /// <inheritdoc/>
  public void StartMerge()
  {
    lock (SyncRoot)
    {
      var mergerThread = Maintenance.StartMergeOperation();
      if (mergerThread == null)
        return;
      MergerThreads.AddOrUpdate(
          mergerThread.ManagedThreadId,
          mergerThread,
          (key, value) => mergerThread);
    }
  }


  /// <inheritdoc/>
  public void StartBottomSegmentsMerge(
      int fromIndex = 0, int toIndex = int.MaxValue)
  {
    lock (SyncRoot)
    {
      var mergerThread = Maintenance
          .StartBottomSegmentsMergeOperation(fromIndex, toIndex);
      if (mergerThread == null)
        return;
      MergerThreads.AddOrUpdate(
          mergerThread.ManagedThreadId,
          mergerThread,
          (key, value) => mergerThread);
    }
  }

  /// <inheritdoc/>
  public void TryCancelBackgroundThreads()
  {
    Maintenance.TryCancelMergeOperation();
    Maintenance.TryCancelBottomSegmentsMergeOperation();
  }

  /// <inheritdoc/>
  public void WaitForBackgroundThreads()
  {
    // Join directly instead of blocking on the async variant.
    // Sync-over-async deadlocks when the caller runs on a single-concurrency
    // TaskScheduler or SynchronizationContext (Orleans grains, UI threads).
    while (true)
    {
      var threads = MergerThreads.ToArray();
      if (threads.Length == 0)
        return;
      Trace($"Waiting {threads.Length} merge threads");
      foreach (var a in threads)
      {
        a.Value.Join();
        // Managed thread ids can be reused,
        // so remove only if the entry still points to the joined thread.
        MergerThreads.TryRemove(a);
      }
      Trace("Wait ended");
    }
  }

  /// <inheritdoc/>
  public async Task WaitForBackgroundThreadsAsync()
  {
    while (true)
    {
      var threads = MergerThreads.ToArray();
      if (threads.Length == 0)
        return;
      Trace($"Waiting {threads.Length} merge threads");
      foreach (var a in threads)
      {
        var t = a.Value;
        if (t.ThreadState != ThreadState.Stopped)
          await Task.Run(t.Join).ConfigureAwait(false);
        MergerThreads.TryRemove(a);
      }
      Trace("Wait ended");
    }
  }

  void StopPeriodicTimer()
  {
    PeriodicTimerCancellationTokenSource.Cancel();
    isPeriodicTimerRunning = false;
    PeriodicTimerCancellationTokenSource = new();
  }

  async Task StartPeriodicTimer()
  {
    if (isPeriodicTimerRunning)
      StopPeriodicTimer();
    isPeriodicTimerRunning = true;
    var cts = PeriodicTimerCancellationTokenSource;
    using var timer = new PeriodicTimer(InactiveBlockCacheCleanupInterval);
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
      if (cts.IsCancellationRequested)
        break;
      var zoneTreeMaintenance = ZoneTree.Maintenance;
      var diskSegment = ZoneTree.Maintenance.DiskSegment;
      var now = Environment.TickCount64;
      var ticks = now - (long)BlockCacheLifeTime.TotalMilliseconds;
      var releasedCount = zoneTreeMaintenance.ReleaseReadBuffers(ticks);
      var releasedCacheKeyRecordCount = zoneTreeMaintenance.ReleaseCircularKeyCacheRecords();
      var releasedCacheValueRecordCount = zoneTreeMaintenance.ReleaseCircularValueCacheRecords();
      Trace($"Released read buffers: {releasedCount}, " +
          $"cached key records: {releasedCacheKeyRecordCount}, " +
          $"cached value records: {releasedCacheValueRecordCount}");
    }
  }

  void Trace(string msg)
  {
    Logger.LogTrace(msg);
  }

  /// <inheritdoc/>
  public void EvictToDisk()
  {
    Maintenance.MoveMutableSegmentForward();
    StartMerge();
  }

  /// <summary>
  /// Waits for tracked merge threads, stops periodic cache cleanup, detaches
  /// event handlers, and disposes this maintainer.
  /// </summary>
  public void Dispose()
  {
    WaitForBackgroundThreads();
    PeriodicTimerCancellationTokenSource.Cancel();
    PeriodicTimerCancellationTokenSource.Dispose();
    Maintenance.OnMutableSegmentMovedForward -= OnMutableSegmentMovedForward;
    Maintenance.OnDiskSegmentCreated -= OnDiskSegmentCreated;
    Maintenance.OnMergeOperationEnded -= OnMergeOperationEnded;
    Maintenance.OnZoneTreeIsDisposing -= OnZoneTreeIsDisposing;
    Maintenance.OnBottomSegmentsMergeOperationEnded -= OnBottomSegmentsMergeOperationEnded;
  }
}
