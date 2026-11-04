namespace ZoneTree.Cluster;

internal abstract class ColumnView : IDisposable
{
  internal Column Column;
  internal virtual bool ConflictDetected => false;
  internal virtual bool HasWrites => false;
  internal virtual bool CanCommit() => true;
  internal abstract Task<bool> BeginCommit(bool durable);
  public abstract void Dispose();
}
