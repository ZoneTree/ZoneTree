namespace ZoneTree.Transactional.Distributed;

internal abstract class DistributedDatabaseColumnView : IDisposable
{
  internal DistributedDatabaseColumn Column;
  internal virtual bool ConflictDetected => false;
  internal virtual bool HasWrites => false;
  internal virtual bool CanCommit() => true;
  internal abstract Task<bool> BeginCommit(bool durable);
  public abstract void Dispose();
}
