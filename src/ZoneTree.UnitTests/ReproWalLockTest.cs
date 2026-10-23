using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class ReproWalLockTest
{
  [Test]
  public void ReopenWhileThreadPoolIsBusy()
  {
    var dataPath = "data/ReproWalLockTest";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    {
      using var data = new ZoneTreeFactory<int, string>()
          .SetDataDirectory(dataPath)
          .OpenOrCreate();
      data.TryAtomicAdd(1, "1", out _);
      data.Maintenance.MoveMutableSegmentForward();
    }

    // Occupy every thread-pool worker so MarkFrozen's Task.Run is delayed,
    // as it can be on a loaded CI runner.
    ThreadPool.GetMinThreads(out var workers, out _);
    using var release = new ManualResetEventSlim();
    for (var i = 0; i < workers; ++i)
      ThreadPool.QueueUserWorkItem(_ => release.Wait());
    try
    {
      for (var i = 0; i < 3; ++i)
      {
        using var data = new ZoneTreeFactory<int, string>()
            .SetDataDirectory(dataPath)
            .Open();
        Assert.That(data.ContainsKey(1), Is.True);
      }
    }
    finally
    {
      release.Set();
    }
  }
}
