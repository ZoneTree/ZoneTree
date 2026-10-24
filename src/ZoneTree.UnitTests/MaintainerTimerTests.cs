namespace ZoneTree.UnitTests;

public sealed class MaintainerTimerTests
{
  [Test]
  public void MaintainerDisposedBeforeTimerTaskStartsDoesNotFailInBackground()
  {
    var dataPath = "data/MaintainerDisposedBeforeTimerTaskStartsDoesNotFailInBackground";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    var disposedTokenSourceErrors = 0;
    void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
      if (e.Exception.InnerExceptions.Any(x =>
          x is ObjectDisposedException &&
          x.Message.Contains(nameof(CancellationTokenSource), StringComparison.Ordinal)))
      {
        Interlocked.Increment(ref disposedTokenSourceErrors);
        e.SetObserved();
      }
    }

    TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    try
    {
      using var zoneTree = new ZoneTreeFactory<int, int>()
          .SetDataDirectory(dataPath)
          .OpenOrCreate();

      // Queue more blocking work items than there are pool threads so every
      // worker is blocked and some items stay queued. The maintainer's timer
      // task is queued behind them and can start only after the maintainer
      // is disposed and the workers are released.
      ThreadPool.GetMinThreads(out var minWorkers, out _);
      using var release = new ManualResetEventSlim();
      var blockers = ThreadPool.ThreadCount + minWorkers + 1;
      for (var i = 0; i < blockers; ++i)
        ThreadPool.QueueUserWorkItem(_ => release.Wait());
      Thread.Sleep(100);
      Assume.That(ThreadPool.PendingWorkItemCount, Is.GreaterThan(0));

      var maintainer = zoneTree.CreateMaintainer();
      maintainer.Dispose();
      release.Set();

      // The timer task was queued before this marker, so it has started once the marker runs.
      using var markerDone = new ManualResetEventSlim();
      ThreadPool.QueueUserWorkItem(_ => markerDone.Set());
      markerDone.Wait();
      Thread.Sleep(100);

      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
    }
    finally
    {
      TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    Assert.That(disposedTokenSourceErrors, Is.Zero);
  }
}
