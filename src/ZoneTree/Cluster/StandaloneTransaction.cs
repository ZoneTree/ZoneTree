using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>A writing transaction of a standalone column, over the transaction of its MVCC tree.</summary>
internal sealed class StandaloneTransaction<TKey, TValue> :
    StandaloneReadTransaction<TKey, TValue>, IStandaloneTransaction<TKey, TValue>
{
  readonly ITransaction<TKey, TValue> Writer;

  internal StandaloneTransaction(ZoneTreeCluster cluster, ITransaction<TKey, TValue> transaction)
      : base(cluster, transaction) => Writer = transaction;

  public long TransactionId => Writer.TransactionId;
  public long CommitTimestamp => Writer.CommitTimestamp;
  public TransactionIsolationLevel IsolationLevel => Writer.IsolationLevel;
  public TransactionState State => Writer.State;
  public bool HasConflict => Writer.HasConflict;

  public bool TryGetForUpdate(in TKey key, out TValue value) => Writer.TryGetForUpdate(in key, out value);

  public void Upsert(in TKey key, in TValue value) => Writer.Upsert(in key, in value);

  public void Delete(in TKey key) => Writer.Delete(in key);

  public ValueTask<ReadResult<TValue>> TryGetForUpdateAsync(TKey key, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => new ReadResult<TValue>(TryGetForUpdate(in key, out var value), value), cancellationToken);

  public ValueTask UpsertAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => Upsert(in key, in value), cancellationToken);

  public ValueTask DeleteAsync(TKey key, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => Delete(in key), cancellationToken);

  public void Commit()
  {
    try { Writer.Commit(); }
    finally { EndIfFinished(); }
  }

  public bool TryCommit()
  {
    try { return Writer.TryCommit(); }
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
      => EndWhenFinished(Writer.CommitDurableAsync(cancellationToken));

  public Task<bool> TryCommitDurableAsync(CancellationToken cancellationToken = default)
  {
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
