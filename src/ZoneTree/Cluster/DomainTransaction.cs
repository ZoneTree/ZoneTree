using ZoneTree.Transactional;
using System.Runtime.ExceptionServices;
using ZoneTree.Exceptions;

namespace ZoneTree.Cluster;

internal sealed class DomainTransaction : DomainReadTransaction, IDomainTransaction
{
  bool CommitConflict;
  public long TransactionId { get; }
  public long CommitTimestamp { get; private set; }
  public TransactionIsolationLevel IsolationLevel { get; }
  public TransactionState State => state;
  protected override bool IsWriting => true;
  protected override TransactionIsolationLevel Isolation => IsolationLevel;

  public bool HasConflict
  {
    get
    {
      if (CommitConflict || FirstView?.ConflictDetected == true)
        return true;
      if (OtherViews != null)
        foreach (var view in OtherViews)
          if (view.ConflictDetected)
            return true;
      return false;
    }
  }

  internal DomainTransaction(ClusterDomain domain, long id, long snapshot,
      TransactionIsolationLevel isolation) : base(domain, snapshot)
  {
    TransactionId = id;
    IsolationLevel = isolation;
  }

  public new IWriteColumn<TKey, TValue> GetColumn<TKey, TValue>(ICoordinatedColumn<TKey, TValue> column)
      => (IWriteColumn<TKey, TValue>)base.GetColumn(column);

  public void Commit()
  {
    if (!TryCommit())
      throw new TransactionConflictException(TransactionId);
  }

  public bool TryCommit() => BeginCommit(false).GetAwaiter().GetResult();

  public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
  {
    if (!await TryCommitAsync(cancellationToken).ConfigureAwait(false))
      throw new TransactionConflictException(TransactionId);
  }

  public ValueTask<bool> TryCommitAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var commit = BeginCommit(false);
    return new(cancellationToken.CanBeCanceled ? commit.WaitAsync(cancellationToken) : commit);
  }

  public async Task CommitDurableAsync(CancellationToken cancellationToken = default)
  {
    if (!await TryCommitDurableAsync(cancellationToken).ConfigureAwait(false))
      throw new TransactionConflictException(TransactionId);
  }

  public Task<bool> TryCommitDurableAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var commit = BeginCommit(true);
    return cancellationToken.CanBeCanceled ? commit.WaitAsync(cancellationToken) : commit;
  }

  Task<bool> BeginCommit(bool durable)
  {
    EnsureActive();
    if (FirstView == null)
      return CompleteCommit(false);
    if (OtherViews == null)
      return BeginSingleCommit(durable);

    var views = new ColumnView[OtherViews.Count + 1];
    views[0] = FirstView;
    OtherViews.CopyTo(views, 1);
    Array.Sort(views, static (left, right) => left.Column.Index.CompareTo(right.Column.Index));
    var entered = 0;
    try
    {
      for (; entered < views.Length; ++entered)
        Monitor.Enter(views[entered].Column.CommitGate);
      foreach (var view in views)
        if (!view.CanCommit())
          return AbortConflict();
      state = TransactionState.Committing;
      var commits = new Task<bool>[views.Length];
      Array.Fill(commits, Task.FromResult(true));
      var hasWrites = false;
      Exception startError = null;
      for (var index = 0; index < views.Length; ++index)
      {
        hasWrites |= views[index].HasWrites;
        try { commits[index] = views[index].BeginCommit(durable); }
        catch (Exception error) { startError = error; break; }
      }
      if (startError == null && Array.TrueForAll(commits, static commit => commit.IsCompletedSuccessfully && commit.Result))
        return CompleteCommit(hasWrites);
      return FinishCommitsAsync(commits, hasWrites, startError);
    }
    catch
    {
      if (state == TransactionState.Active)
        RollbackCore();
      throw;
    }
    finally
    {
      for (var index = entered - 1; index >= 0; --index)
        Monitor.Exit(views[index].Column.CommitGate);
    }
  }

  Task<bool> BeginSingleCommit(bool durable)
  {
    lock (FirstView.Column.CommitGate)
    {
      try
      {
        state = TransactionState.Committing;
        var hasWrites = FirstView.HasWrites;
        var commit = FirstView.BeginCommit(durable);
        if (commit.IsCompletedSuccessfully)
          return IsCommitted(commit) ? CompleteCommit(hasWrites) : AbortConflict();
        return FinishSingleCommitAsync(commit, hasWrites);
      }
      catch
      {
        if (state == TransactionState.Active)
          RollbackCore();
        else if (state == TransactionState.Committing)
          FailCommit();
        throw;
      }
    }
  }

  async Task<bool> FinishSingleCommitAsync(Task<bool> commit, bool hasWrites)
  {
    try
    {
      if (!await commit.ConfigureAwait(false))
        return await AbortConflict().ConfigureAwait(false);
      return await CompleteCommit(hasWrites).ConfigureAwait(false);
    }
    catch { FailCommit(); throw; }
  }

  async Task<bool> FinishCommitsAsync(Task<bool>[] commits, bool hasWrites, Exception startError)
  {
    try
    {
      var results = await Task.WhenAll(commits).ConfigureAwait(false);
      if (startError != null)
        ExceptionDispatchInfo.Capture(startError).Throw();
      // A column rejected its commit after validation, and other columns may have committed.
      if (Array.Exists(results, static result => !result))
        throw new TransactionInDoubtException(TransactionId);
      return await CompleteCommit(hasWrites).ConfigureAwait(false);
    }
    catch { FailCommit(); throw; }
  }

  Task<bool> CompleteCommit(bool hasWrites)
  {
    CommitTimestamp = hasWrites ? Domain.TransactionCommitted() : SnapshotTimestamp;
    state = TransactionState.Committed;
    Close();
    return Task.FromResult(true);
  }

  Task<bool> AbortConflict()
  {
    CommitConflict = true;
    state = TransactionState.Active;
    RollbackCore();
    return Task.FromResult(false);
  }

  static bool IsCommitted(Task<bool> commit)
      => commit.IsCompletedSuccessfully && commit.GetAwaiter().GetResult();

  protected override void ThrowNotActive() => throw state switch
  {
    TransactionState.Committed => (Exception)new TransactionAlreadyCommittedException(TransactionId),
    TransactionState.Committing => new TransactionCommitInProgressException(TransactionId),
    TransactionState.InDoubt => new TransactionInDoubtException(TransactionId),
    _ => new TransactionAbortedException(TransactionId),
  };

  void FailCommit()
  {
    if (state != TransactionState.Committed)
      state = TransactionState.InDoubt;
    Close();
  }

  public void Rollback() => Dispose();
  public ValueTask RollbackAsync() => DisposeAsync();
}
