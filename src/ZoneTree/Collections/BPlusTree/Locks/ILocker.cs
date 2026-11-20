#undef USE_NODE_IDS

namespace ZoneTree.Collections.BPlusTreeLocks;

public interface ILocker
{
  bool TryEnterWriteLock(int millisecondsTimeout);

  void WriteLock();

  void WriteUnlock();

  void ReadLock();

  void ReadUnlock();
}
