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

      // Every pool thread blocks, and work stays queued behind them, so the maintainer's timer task,
      // queued after that work, starts only once the workers are released, after the maintainer is
      // disposed.
      using var pool = new BlockedThreadPool();
      var maintainer = zoneTree.CreateMaintainer();
      maintainer.Dispose();
      pool.Release();

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

  /// <summary>
  /// Blocks every thread of the pool, and keeps work queued behind them. The pool takes queued work while a
  /// thread is free, and adds threads as its workers block, so it is given work until some stays queued
  /// through a pause: then no thread is free, and the pool adds them too slowly to reach the work queued
  /// last before the threads are released.
  /// </summary>
  sealed class BlockedThreadPool : IDisposable
  {
    const int Queued = 16;
    static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(50);
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    readonly ManualResetEventSlim Released = new();
    int queued;
    int returned;

    internal BlockedThreadPool()
    {
      var deadline = DateTime.UtcNow + Limit;
      while (true)
      {
        FillQueue();
        // A free thread takes queued work within the pause; once none is free, the work stays queued.
        Thread.Sleep(Pause);
        if (ThreadPool.PendingWorkItemCount >= Queued / 2)
          break;
        if (DateTime.UtcNow > deadline)
        {
          Dispose();
          Assert.Fail($"The thread pool kept taking the work queued for {Limit.TotalSeconds} seconds.");
        }
      }
      FillQueue();
    }

    void FillQueue()
    {
      while (ThreadPool.PendingWorkItemCount < Queued)
      {
        Interlocked.Increment(ref queued);
        ThreadPool.QueueUserWorkItem(_ =>
        {
          Released.Wait();
          Interlocked.Increment(ref returned);
        });
      }
    }

    /// <summary>Releases the blocked threads.</summary>
    internal void Release() => Released.Set();

    /// <summary>Releases the threads and waits until every piece of work returned, so none waits on the event once it is gone.</summary>
    public void Dispose()
    {
      Released.Set();
      SpinWait.SpinUntil(() => Volatile.Read(ref returned) == Volatile.Read(ref queued));
      Released.Dispose();
    }
  }
}
