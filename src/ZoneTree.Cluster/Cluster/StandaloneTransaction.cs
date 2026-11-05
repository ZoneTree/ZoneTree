using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>A writing transaction of a standalone column, over the transaction of its MVCC tree.</summary>
/// <remarks>
/// The transaction registers with the cluster, which rolls it back when it closes. Its
/// operations and commits report a drop of the column.
/// </remarks>
internal sealed class StandaloneTransaction<TKey, TValue> :
    StandaloneReadTransaction<TKey, TValue>, IStandaloneTransaction<TKey, TValue>, IClusterOpenTransaction
{
  readonly ITransaction<TKey, TValue> Writer;

  internal StandaloneTransaction(Column column, ITransaction<TKey, TValue> transaction, TimeSpan timeout)
      : base(column, transaction)
  {
    Writer = transaction;
    Cluster.TransactionStarted(this);
    ExpiryTimer = TransactionExpiry.Start(timeout, Expire);
  }

  protected override void OnEnded() => Cluster.TransactionEnded(this);

  public void RollbackOnClose() => Dispose();

  void Expire()
  {
    if (Writer.State != TransactionState.Active)
      return;
    Writer.Rollback();
    if (Writer.State == TransactionState.Aborted)
      Cluster.Logger.LogWarning($"Transaction {TransactionId} of a standalone column timed out and was rolled back.");
    EndIfFinished();
  }

  public long TransactionId => Writer.TransactionId;
  public long CommitTimestamp => Writer.CommitTimestamp;
  public TransactionIsolationLevel IsolationLevel => Writer.IsolationLevel;
  public TransactionState State => Writer.State;
  public bool HasConflict => Writer.HasConflict;

  SchemaObjectDroppedException Dropped() => new($"Column '{Column.Name}' was dropped.");

  /// <summary>The writing MVCC transaction, once the column is known not to be dropped.</summary>
  ITransaction<TKey, TValue> ActiveWriter
  {
    get
    {
      Column.ThrowIfDropped();
      return Writer;
    }
  }

  public bool TryGetForUpdate(in TKey key, out TValue value) => ActiveWriter.TryGetForUpdate(in key, out value);

  public void Upsert(in TKey key, in TValue value) => ActiveWriter.Upsert(in key, in value);

  public void Delete(in TKey key) => ActiveWriter.Delete(in key);

  public ValueTask<ReadResult<TValue>> TryGetForUpdateAsync(TKey key, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => new ReadResult<TValue>(TryGetForUpdate(in key, out var value), value), cancellationToken);

  public ValueTask UpsertAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => Upsert(in key, in value), cancellationToken);

  public ValueTask DeleteAsync(TKey key, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => Delete(in key), cancellationToken);

  public void Commit()
  {
    try { ActiveWriter.Commit(); }
    finally { EndIfFinished(); }
  }

  public bool TryCommit()
  {
    try { return ActiveWriter.TryCommit(); }
    finally { EndIfFinished(); }
  }

  public ValueTask CommitAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    try
    {
      Commit();
      return ValueTask.CompletedTask;
    }
    catch (Exception error)
    {
      return ValueTask.FromException(error);
    }
  }

  public ValueTask<bool> TryCommitAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    try
    {
      return ValueTask.FromResult(TryCommit());
    }
    catch (Exception error)
    {
      return ValueTask.FromException<bool>(error);
    }
  }

  public Task CommitDurableAsync(CancellationToken cancellationToken = default)
      => Column.IsDropped
          ? Task.FromException(Dropped())
          : EndWhenFinished(Writer.CommitDurableAsync(cancellationToken));

  public Task<bool> TryCommitDurableAsync(CancellationToken cancellationToken = default)
  {
    if (Column.IsDropped)
      return Task.FromException<bool>(Dropped());
    var commit = Writer.TryCommitDurableAsync(cancellationToken);
    EndWhenFinished(commit);
    return commit;
  }

  public void Rollback()
  {
    try { Writer.Rollback(); }
    finally { EndIfFinished(); }
  }

  public ValueTask RollbackAsync()
  {
    Rollback();
    return ValueTask.CompletedTask;
  }

  void EndIfFinished()
  {
    if (Writer.State != TransactionState.Active && Writer.State != TransactionState.Committing)
      End();
  }

  // The caller waits on the commit task itself; the tracking continuation only stops
  // tracking the transaction and never delays the caller.
  Task EndWhenFinished(Task commit)
  {
    if (commit.IsCompleted)
      EndIfFinished();
    else
      commit.ContinueWith(_ => EndIfFinished(), CancellationToken.None,
          TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    return commit;
  }
}
