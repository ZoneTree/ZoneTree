namespace ZoneTree.Collections.BPlusTreeLocks;

#pragma warning disable CA1001

public sealed class ReadWriteLock : ILocker
{
  readonly ReaderWriterLockSlim Locker = new(LockRecursionPolicy.SupportsRecursion);

  public void WriteLock()
  {
    Locker.EnterWriteLock();
  }

  public void ReadLock()
  {
    Locker.EnterReadLock();
  }

  public void WriteUnlock()
  {
    Locker.ExitWriteLock();
  }

  public void ReadUnlock()
  {
    Locker.ExitReadLock();
  }

  public bool TryEnterWriteLock(int millisecondsTimeout)
  {
    return Locker.TryEnterWriteLock(millisecondsTimeout);
  }

  ~ReadWriteLock() => Locker.Dispose();
}

#pragma warning restore CA1001
