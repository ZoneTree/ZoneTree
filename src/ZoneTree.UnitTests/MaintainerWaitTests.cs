using ZoneTree.Comparers;

namespace ZoneTree.UnitTests;

public sealed class MaintainerWaitTests
{
  static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

  [Test]
  public void WaitForBackgroundThreadsDoesNotDeadlockOnExclusiveScheduler()
  {
    RunOnExclusiveSchedulerWhileMergeIsRunning(
        "data/WaitForBackgroundThreadsDoesNotDeadlockOnExclusiveScheduler",
        maintainer => maintainer.WaitForBackgroundThreads(),
        disposesMaintainer: false);
  }

  [Test]
  public void MaintainerDisposeDoesNotDeadlockOnExclusiveScheduler()
  {
    RunOnExclusiveSchedulerWhileMergeIsRunning(
        "data/MaintainerDisposeDoesNotDeadlockOnExclusiveScheduler",
        maintainer => maintainer.Dispose(),
        disposesMaintainer: true);
  }

  [Test]
  public void WaitForBackgroundThreadsAsyncCompletesOnExclusiveScheduler()
  {
    RunOnExclusiveSchedulerWhileMergeIsRunning(
        "data/WaitForBackgroundThreadsAsyncCompletesOnExclusiveScheduler",
        maintainer => maintainer
            .WaitForBackgroundThreadsAsync()
            .GetAwaiter()
            .GetResult(),
        disposesMaintainer: false);
  }

  /// <summary>
  /// Holds a merge thread open, runs the wait on a single-concurrency
  /// scheduler (the shape of an Orleans activation scheduler),
  /// then releases the merge and expects the wait to finish.
  /// </summary>
  static void RunOnExclusiveSchedulerWhileMergeIsRunning(
      string dataPath,
      Action<IMaintainer> wait,
      bool disposesMaintainer)
  {
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    var comparer = new GatedComparer();
    using var zoneTree = new ZoneTreeFactory<int, int>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDataDirectory(dataPath)
        .SetComparer(comparer)
        .OpenOrCreate();
    var maintainer = zoneTree.CreateMaintainer();
    maintainer.ThresholdForMergeOperationStart = int.MaxValue;
    maintainer.EnableJobForCleaningInactiveCaches = false;

    // Two overlapping read-only segments force key comparisons in the merge.
    for (var i = 0; i < 100; ++i)
      zoneTree.Upsert(i, i + 1);
    zoneTree.Maintenance.MoveMutableSegmentForward();
    for (var i = 50; i < 150; ++i)
      zoneTree.Upsert(i, i + 1);
    zoneTree.Maintenance.MoveMutableSegmentForward();

    comparer.Arm();
    maintainer.StartMerge();
    Assert.That(comparer.Entered.Wait(Timeout), Is.True,
        "The merge thread did not reach the comparer.");

    var exclusiveScheduler =
        new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
    var waitTask = Task.Factory.StartNew(
        () => wait(maintainer),
        CancellationToken.None,
        TaskCreationOptions.None,
        exclusiveScheduler);

    // Let the wait block on the still-running merge thread before releasing it.
    Thread.Sleep(200);
    Assert.That(waitTask.IsCompleted, Is.False);
    comparer.Release.Set();

    Assert.That(waitTask.Wait(Timeout), Is.True,
        "Waiting for the merge thread deadlocked on the exclusive scheduler.");
    Assert.That(zoneTree.Maintenance.ReadOnlySegmentsCount, Is.EqualTo(0));
    for (var i = 0; i < 150; ++i)
      Assert.That(zoneTree.ContainsKey(i), Is.True);

    if (!disposesMaintainer)
      maintainer.Dispose();
    zoneTree.Maintenance.Drop();
  }

  sealed class GatedComparer : IRefComparer<int>
  {
    volatile bool Armed;

    public ManualResetEventSlim Entered { get; } = new();

    public ManualResetEventSlim Release { get; } = new();

    public void Arm() => Armed = true;

    public int Compare(in int x, in int y)
    {
      if (Armed)
      {
        Entered.Set();
        Release.Wait();
      }
      return x.CompareTo(y);
    }
  }
}
