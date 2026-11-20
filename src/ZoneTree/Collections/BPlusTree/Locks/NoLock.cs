namespace ZoneTree.Collections.BPlusTreeLocks;

public sealed class NoLock : ILocker
{
  public readonly static NoLock Instance = new();

  NoLock()
  {
  }

  public void WriteLock()
  {
  }

  public void WriteUnlock()
  {
  }

  public void ReadLock()
  {
  }

  public void ReadUnlock()
  {
  }

  public bool TryEnterWriteLock(int millisecondsTimeout)
  {
    return true;
  }
}
