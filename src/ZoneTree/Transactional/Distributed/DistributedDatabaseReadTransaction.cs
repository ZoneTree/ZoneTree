namespace ZoneTree.Transactional.Distributed;

internal class DistributedDatabaseReadTransaction : IDistributedReadTransaction
{
  protected readonly DistributedDatabase Database;
  protected DistributedDatabaseColumnView FirstView;
  protected List<DistributedDatabaseColumnView> OtherViews;
  protected volatile TransactionState state = TransactionState.Active;
  int Ended;

  public long SnapshotTimestamp { get; }
  protected virtual bool IsWriting => false;
  protected virtual TransactionIsolationLevel Isolation => TransactionIsolationLevel.Snapshot;

  internal DistributedDatabaseReadTransaction(DistributedDatabase database, long snapshot)
  {
    Database = database;
    SnapshotTimestamp = snapshot;
    database.TransactionStarted();
  }

  public IDistributedReadColumn<TKey, TValue> GetColumn<TKey, TValue>(IDistributedColumn<TKey, TValue> column)
  {
    EnsureActive();
    if (column is not DistributedDatabaseColumn<TKey, TValue> local || local.Database != Database)
      throw new ArgumentException("The column belongs to another database.", nameof(column));
    if (FirstView == null)
      FirstView = local.CreateView(IsWriting, Isolation);
    if (FirstView.Column == local)
      return (IDistributedReadColumn<TKey, TValue>)FirstView;
    if (OtherViews != null)
      foreach (var view in OtherViews)
        if (view.Column == local)
          return (IDistributedReadColumn<TKey, TValue>)view;
    var created = local.CreateView(IsWriting, Isolation);
    (OtherViews ??= []).Add(created);
    return (IDistributedReadColumn<TKey, TValue>)created;
  }

  protected void EnsureActive()
  {
    if (state != TransactionState.Active)
      throw new InvalidOperationException($"The database transaction is {state}.");
    Database.ThrowIfDisposed();
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
    finally { Database.TransactionEnded(); }
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
