using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal class DomainReadTransaction : IDomainReadTransaction, IClusterOpenTransaction
{
  protected readonly ZoneTreeCluster Cluster;
  protected readonly ClusterDomain Domain;

  /// <summary>
  /// Guards the views and the claim on the end of the transaction, so a view is
  /// never added or left out while a commit or rollback takes the views.
  /// </summary>
  protected readonly Lock ViewLock = new();

  protected ColumnView FirstView;
  protected List<ColumnView> OtherViews;

  /// <summary>
  /// True once a commit or rollback claimed the end of the transaction. No view is added
  /// after it. Guarded by the view lock.
  /// </summary>
  bool EndClaimed;

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
    lock (ViewLock)
    {
      if (EndClaimed)
      {
        EnsureActive();
        ThrowEnding();
      }
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
  }

  protected void EnsureActive()
  {
    if (state != TransactionState.Active)
      ThrowNotActive();
    Cluster.ThrowIfDisposed();
  }

  /// <summary>Throws for a transaction that ended. A read-only transaction ends when it is disposed.</summary>
  protected virtual void ThrowNotActive() => ObjectDisposedException.ThrowIf(true, this);

  /// <summary>Throws for a call that starts while a commit of an active transaction runs.</summary>
  protected virtual void ThrowEnding() => ObjectDisposedException.ThrowIf(true, this);

  /// <summary>
  /// Claims the end of the transaction for a commit or a rollback and returns its views.
  /// Returns null if another commit or rollback claimed it. A rollback leaves the
  /// transaction aborted at once.
  /// </summary>
  protected ColumnView[] TryClaimEnd(bool rollback)
  {
    lock (ViewLock)
    {
      if (EndClaimed || state != TransactionState.Active)
        return null;
      EndClaimed = true;
      if (rollback)
        state = TransactionState.Aborted;
      if (FirstView == null)
        return [];
      var views = new ColumnView[(OtherViews?.Count ?? 0) + 1];
      views[0] = FirstView;
      OtherViews?.CopyTo(views, 1);
      return views;
    }
  }

  /// <summary>Disposes the views. Called after a commit or rollback claimed the end.</summary>
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
    if (TryClaimEnd(rollback: true) != null)
      Close();
  }

  /// <summary>Rolls back a transaction whose commit claimed its end and cannot finish.</summary>
  protected void RollbackClaimed()
  {
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
