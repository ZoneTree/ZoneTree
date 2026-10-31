namespace ZoneTree.Collections.BTree.Lock;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2002:Do not lock on objects with weak identity", Justification = "Best memory footprint for BTree nodes. Avoid allocation of a new lock object per BTree node.")]
public sealed class MonitorLock : ILocker
{
  // Nodes are held very briefly, so writers on the same leaf get it sooner by
  // spinning than by blocking, and blocked threads that wake up one by one slow
  // down everyone behind them. The spin ends soon, so a long wait still blocks.
  const int SpinCount = 16;

  const int MaximumSpinWaitIterations = 64;

  public void WriteLock()
  {
    if (Monitor.TryEnter(this))
      return;
    var iterations = 1;
    for (var i = 0; i < SpinCount; ++i)
    {
      Thread.SpinWait(iterations);
      if (Monitor.TryEnter(this))
        return;
      if (iterations < MaximumSpinWaitIterations)
        iterations *= 2;
    }
    Monitor.Enter(this);
  }

  public void WriteUnlock()
  {
    Monitor.Exit(this);
  }

  public void ReadLock()
  {
    WriteLock();
  }

  public void ReadUnlock()
  {
    Monitor.Exit(this);
  }

  public bool TryEnterWriteLock(int millisecondsTimeout)
  {
    return Monitor.TryEnter(this, millisecondsTimeout);
  }
}
