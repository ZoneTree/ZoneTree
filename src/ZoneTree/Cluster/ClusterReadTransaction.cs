using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal class ClusterReadTransaction : IClusterReadTransaction
{
  protected readonly ZoneTreeCluster Cluster;
  protected ColumnView FirstView;
  protected List<ColumnView> OtherViews;
  protected volatile TransactionState state = TransactionState.Active;
  int Ended;

  public long SnapshotTimestamp { get; }
  protected virtual bool IsWriting => false;
  protected virtual TransactionIsolationLevel Isolation => TransactionIsolationLevel.Snapshot;

  internal ClusterReadTransaction(ZoneTreeCluster cluster, long snapshot)
  {
    Cluster = cluster;
    SnapshotTimestamp = snapshot;
    cluster.TransactionStarted();
  }

  public IReadColumn<TKey, TValue> GetColumn<TKey, TValue>(ICoordinatedColumn<TKey, TValue> column)
  {
    EnsureActive();
    if (column is not CoordinatedColumn<TKey, TValue> local || local.Cluster != Cluster)
      throw new ArgumentException("The column belongs to another cluster.", nameof(column));
    if (FirstView == null)
      FirstView = local.CreateView(IsWriting, Isolation);
    if (FirstView.Column == local)
      return (IReadColumn<TKey, TValue>)FirstView;
    if (OtherViews != null)
      foreach (var view in OtherViews)
        if (view.Column == local)
          return (IReadColumn<TKey, TValue>)view;
    var created = local.CreateView(IsWriting, Isolation);
    (OtherViews ??= []).Add(created);
    return (IReadColumn<TKey, TValue>)created;
  }

  protected void EnsureActive()
  {
    if (state != TransactionState.Active)
      throw new InvalidOperationException($"The cluster transaction is {state}.");
    Cluster.ThrowIfDisposed();
  }

  protected void Close()
  {
    if (Interlocked.Exchange(ref Ended, 1) != 0)
      return;
    List<Exception> errors = null;
    try
    {
      if (FirstView != null)
      {
        try { FirstView.Dispose(); }
        catch (Exception error) { errors = [error]; }
      }
      if (OtherViews != null)
        foreach (var view in OtherViews)
        {
          try { view.Dispose(); }
          catch (Exception error) { (errors ??= []).Add(error); }
        }
    }
    finally { Cluster.TransactionEnded(); }
    if (errors != null)
      throw new AggregateException(errors);
  }

  protected void RollbackCore()
  {
    if (state != TransactionState.Active)
      return;
    state = TransactionState.Aborted;
    Close();
  }

  public void Dispose() => RollbackCore();

  public ValueTask DisposeAsync()
  {
    Dispose();
    return ValueTask.CompletedTask;
  }
}
