namespace ZoneTree.Cluster;

internal sealed class ClusterMaintainer : IMaintainer
{
  readonly IMaintainer[] Maintainers;

  internal ClusterMaintainer(Column[] columns)
  {
    Maintainers = new IMaintainer[columns.Length];
    var created = 0;
    try
    {
      for (; created < columns.Length; ++created)
        Maintainers[created] = columns[created].CreateMaintainer();
    }
    catch
    {
      for (var index = created - 1; index >= 0; --index)
        Maintainers[index].Dispose();
      throw;
    }
  }

  public int ThresholdForMergeOperationStart
  {
    get => Maintainers[0].ThresholdForMergeOperationStart;
    set { foreach (var maintainer in Maintainers) maintainer.ThresholdForMergeOperationStart = value; }
  }
  public int MaximumReadOnlySegmentCount
  {
    get => Maintainers[0].MaximumReadOnlySegmentCount;
    set { foreach (var maintainer in Maintainers) maintainer.MaximumReadOnlySegmentCount = value; }
  }
  public bool EnableJobForCleaningInactiveCaches
  {
    get => Maintainers[0].EnableJobForCleaningInactiveCaches;
    set { foreach (var maintainer in Maintainers) maintainer.EnableJobForCleaningInactiveCaches = value; }
  }
  public TimeSpan BlockCacheLifeTime
  {
    get => Maintainers[0].BlockCacheLifeTime;
    set { foreach (var maintainer in Maintainers) maintainer.BlockCacheLifeTime = value; }
  }
  public TimeSpan InactiveBlockCacheCleanupInterval
  {
    get => Maintainers[0].InactiveBlockCacheCleanupInterval;
    set { foreach (var maintainer in Maintainers) maintainer.InactiveBlockCacheCleanupInterval = value; }
  }

  public void TryCancelBackgroundThreads() { foreach (var maintainer in Maintainers) maintainer.TryCancelBackgroundThreads(); }
  public void WaitForBackgroundThreads() { foreach (var maintainer in Maintainers) maintainer.WaitForBackgroundThreads(); }
  public Task WaitForBackgroundThreadsAsync() => Task.WhenAll(Maintainers.Select(maintainer => maintainer.WaitForBackgroundThreadsAsync()));
  // Moving a column's mutable segment forward syncs that column's meta WAL file
  // several times. Evicting the columns at once overlaps those disk syncs.
  public void EvictToDisk() => Parallel.ForEach(Maintainers, static maintainer => maintainer.EvictToDisk());
  public void StartMerge() { foreach (var maintainer in Maintainers) maintainer.StartMerge(); }
  public void StartBottomSegmentsMerge(int fromIndex = 0, int toIndex = int.MaxValue)
  {
    foreach (var maintainer in Maintainers)
      maintainer.StartBottomSegmentsMerge(fromIndex, toIndex);
  }
  public void Dispose()
  {
    // Disposal mostly waits for merge threads, and the merges of all columns already
    // run at once. Waiting for one maintainer after another takes no longer.
    List<Exception> errors = null;
    foreach (var maintainer in Maintainers)
    {
      try { maintainer.Dispose(); }
      catch (Exception error) { (errors ??= []).Add(error); }
    }
    if (errors != null)
      throw new AggregateException(errors);
  }
}
