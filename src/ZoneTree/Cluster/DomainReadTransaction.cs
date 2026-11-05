using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal class DomainReadTransaction : IDomainReadTransaction, IClusterOpenTransaction
{
  protected readonly ZoneTreeCluster Cluster;
  protected readonly ClusterDomain Domain;
  protected ColumnView FirstView;
  protected List<ColumnView> OtherViews;
  protected volatile TransactionState state = TransactionState.Active;
  int Ended;

  public long SnapshotTimestamp { get; }
  protected virtual bool IsWriting => false;
  protected virtual TransactionIsolationLevel Isolation => TransactionIsolationLevel.Snapshot;

  internal DomainReadTransaction(ClusterDomain domain, long snapshot)
  {
    Domain = domain;
    Cluster = domain.Cluster;
    SnapshotTimestamp = snapshot;
    Cluster.TransactionStarted(this);
  }

  public IReadColumn<TKey, TValue> GetColumn<TKey, TValue>(ICoordinatedColumn<TKey, TValue> column)
  {
    EnsureActive();
    if (column is not CoordinatedColumn<TKey, TValue> local || local.Domain != Domain)
      throw new ArgumentException("The column belongs to another domain.", nameof(column));
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
      ThrowNotActive();
    Cluster.ThrowIfDisposed();
  }

  /// <summary>Throws for a transaction that ended. A read-only transaction ends when it is disposed.</summary>
  protected virtual void ThrowNotActive() => ObjectDisposedException.ThrowIf(true, this);

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
    finally { Cluster.TransactionEnded(this); }
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

  public void RollbackOnClose() => RollbackCore();

  public void Dispose() => RollbackCore();

  public ValueTask DisposeAsync()
  {
    Dispose();
    return ValueTask.CompletedTask;
  }
}
