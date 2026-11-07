namespace ZoneTree.Cluster;

/// <summary>
/// Maintains a set of open columns: every column of the cluster, including those that open
/// later with the maintainer's current settings, or one column. A dropped column leaves it.
/// </summary>
internal sealed class ClusterMaintainer : IMaintainer
{
  readonly ZoneTreeCluster Cluster;
  readonly Lock MaintainersLock = new();
  readonly Dictionary<Column, IMaintainer> Maintainers = [];
  readonly bool FollowsNewColumns;
  bool Disposed;

  // The defaults of a ZoneTree maintainer, used until a column maintainer reports its own.
  int thresholdForMergeOperationStart;
  int maximumReadOnlySegmentCount = 64;
  bool enableJobForCleaningInactiveCaches;
  TimeSpan blockCacheLifeTime = TimeSpan.FromMinutes(1);
  TimeSpan inactiveBlockCacheCleanupInterval = TimeSpan.FromSeconds(30);

  internal ClusterMaintainer(ZoneTreeCluster cluster, IEnumerable<Column> columns, bool followsNewColumns)
  {
    Cluster = cluster;
    FollowsNewColumns = followsNewColumns;
    try
    {
      foreach (var column in columns)
        Maintainers.Add(column, column.CreateMaintainer());
    }
    catch
    {
      foreach (var maintainer in Maintainers.Values)
        maintainer.Dispose();
      throw;
    }
    if (Maintainers.Count == 0)
      return;
    var first = Maintainers.Values.First();
    thresholdForMergeOperationStart = first.ThresholdForMergeOperationStart;
    maximumReadOnlySegmentCount = first.MaximumReadOnlySegmentCount;
    enableJobForCleaningInactiveCaches = first.EnableJobForCleaningInactiveCaches;
    blockCacheLifeTime = first.BlockCacheLifeTime;
    inactiveBlockCacheCleanupInterval = first.InactiveBlockCacheCleanupInterval;
  }

  /// <summary>Starts maintaining a column that opened after this maintainer was created.</summary>
  internal void Add(Column column)
  {
    lock (MaintainersLock)
    {
      if (Disposed || !FollowsNewColumns)
        return;
      var maintainer = column.CreateMaintainer();
      maintainer.ThresholdForMergeOperationStart = thresholdForMergeOperationStart;
      maintainer.MaximumReadOnlySegmentCount = maximumReadOnlySegmentCount;
      maintainer.BlockCacheLifeTime = blockCacheLifeTime;
      maintainer.InactiveBlockCacheCleanupInterval = inactiveBlockCacheCleanupInterval;
      maintainer.EnableJobForCleaningInactiveCaches = enableJobForCleaningInactiveCaches;
      Maintainers.Add(column, maintainer);
    }
  }

  /// <summary>Stops maintaining a dropped column, waiting for its background work.</summary>
  internal void Remove(Column column)
  {
    IMaintainer maintainer;
    lock (MaintainersLock)
    {
      if (!Maintainers.TryGetValue(column, out maintainer))
        return;
      Maintainers.Remove(column);
    }
    maintainer.Dispose();
  }

  IMaintainer[] Snapshot()
  {
    lock (MaintainersLock)
      return Maintainers.Values.ToArray();
  }

  // The setting is stored under the lock, so a column added meanwhile gets the new value.
  void Set(Action store, Action<IMaintainer> apply)
  {
    lock (MaintainersLock)
    {
      store();
      foreach (var maintainer in Maintainers.Values)
        apply(maintainer);
    }
  }

  public int ThresholdForMergeOperationStart
  {
    get => thresholdForMergeOperationStart;
    set
    {
      Set(() => thresholdForMergeOperationStart = value, maintainer => maintainer.ThresholdForMergeOperationStart = value);
    }
  }

  public int MaximumReadOnlySegmentCount
  {
    get => maximumReadOnlySegmentCount;
    set
    {
      Set(() => maximumReadOnlySegmentCount = value, maintainer => maintainer.MaximumReadOnlySegmentCount = value);
    }
  }

  public bool EnableJobForCleaningInactiveCaches
  {
    get => enableJobForCleaningInactiveCaches;
    set
    {
      Set(() => enableJobForCleaningInactiveCaches = value, maintainer => maintainer.EnableJobForCleaningInactiveCaches = value);
    }
  }

  public TimeSpan BlockCacheLifeTime
  {
    get => blockCacheLifeTime;
    set
    {
      Set(() => blockCacheLifeTime = value, maintainer => maintainer.BlockCacheLifeTime = value);
    }
  }

  public TimeSpan InactiveBlockCacheCleanupInterval
  {
    get => inactiveBlockCacheCleanupInterval;
    set
    {
      Set(() => inactiveBlockCacheCleanupInterval = value, maintainer => maintainer.InactiveBlockCacheCleanupInterval = value);
    }
  }

  public void TryCancelBackgroundThreads()
  {
    foreach (var maintainer in Snapshot())
      maintainer.TryCancelBackgroundThreads();
  }

  public void WaitForBackgroundThreads()
  {
    foreach (var maintainer in Snapshot())
      maintainer.WaitForBackgroundThreads();
  }

  public Task WaitForBackgroundThreadsAsync()
      => Task.WhenAll(Snapshot().Select(maintainer => maintainer.WaitForBackgroundThreadsAsync()));

  // Moving a column's mutable segment forward syncs that column's meta WAL file
  // several times. Evicting the columns at once overlaps those disk syncs.
  public void EvictToDisk() => Parallel.ForEach(Snapshot(), static maintainer => maintainer.EvictToDisk());

  public void StartMerge()
  {
    foreach (var maintainer in Snapshot())
      maintainer.StartMerge();
  }

  public void StartBottomSegmentsMerge(int fromIndex = 0, int toIndex = int.MaxValue)
  {
    foreach (var maintainer in Snapshot())
      maintainer.StartBottomSegmentsMerge(fromIndex, toIndex);
  }

  public void Dispose()
  {
    IMaintainer[] maintainers;
    lock (MaintainersLock)
    {
      if (Disposed)
        return;
      Disposed = true;
      maintainers = Maintainers.Values.ToArray();
    }
    Cluster.RemoveMaintainer(this);
    // Disposal mostly waits for merge threads, and the merges of all columns already
    // run at once. Waiting for one maintainer after another takes no longer.
    List<Exception> errors = null;
    foreach (var maintainer in maintainers)
    {
      try { maintainer.Dispose(); }
      catch (Exception error) { (errors ??= []).Add(error); }
    }
    if (errors != null)
      throw new AggregateException(errors);
  }
}
