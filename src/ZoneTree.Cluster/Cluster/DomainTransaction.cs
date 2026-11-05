using ZoneTree.Transactional;
using ZoneTree.Exceptions;

namespace ZoneTree.Cluster;

/// <summary>
/// A writing transaction of a domain. Its writes in every column of the domain commit
/// together, at one commit timestamp, or not at all.
/// </summary>
/// <remarks>
/// <para>
/// The commit takes the commit gates of the transaction's columns in column id order,
/// validates every view, takes one timestamp from the domain's sequencer and writes the
/// commit record of every view that wrote. It then publishes the views. Readers see the
/// commit in every column at once, when the domain's snapshot reaches its timestamp.
/// </para>
/// <para>
/// A failure before the commit records are complete rolls back every view. A durable
/// commit is published only after the domain made it durable; a failed domain sync leaves
/// it in doubt.
/// </para>
/// </remarks>
internal sealed class DomainTransaction : DomainReadTransaction, IDomainTransaction
{
  readonly DomainSnapshots.Writer Writer;
  bool CommitConflict;
  public long TransactionId { get; }
  public long CommitTimestamp { get; private set; }
  public TransactionIsolationLevel IsolationLevel { get; }
  public TransactionState State => state;
  protected override bool IsWriting => true;
  protected override TransactionIsolationLevel Isolation => IsolationLevel;
  protected override long ViewTransactionId => TransactionId;

  public bool HasConflict
  {
    get
    {
      if (CommitConflict)
        return true;
      lock (ViewLock)
      {
        if (FirstView?.ConflictDetected == true)
          return true;
        if (OtherViews != null)
          foreach (var view in OtherViews)
            if (view.ConflictDetected)
              return true;
      }
      return false;
    }
  }

  internal DomainTransaction(ClusterDomain domain, DomainSnapshots.Writer writer,
      ClusterTransactionOptions options) : base(domain, writer.SnapshotTimestamp)
  {
    Writer = writer;
    TransactionId = writer.TransactionId;
    IsolationLevel = options.IsolationLevel;
    ExpiryTimer = TransactionExpiry.Start(options.Timeout, Expire);
  }

  void Expire()
  {
    if (RollbackCore())
      Cluster.Logger.LogWarning($"Transaction {TransactionId} of domain '{Domain.Name}' timed out and was rolled back.");
  }

  public new IWriteColumn<TKey, TValue> GetColumn<TKey, TValue>(ICoordinatedColumn<TKey, TValue> column)
      => (IWriteColumn<TKey, TValue>)base.GetColumn(column);

  public void Commit()
  {
    if (!TryCommit())
      throw new TransactionConflictException(TransactionId);
  }

  // The commit waits on this thread, so a blocked caller never needs a thread pool thread.
  public bool TryCommit() => BeginCommit(false, synchronous: true).GetAwaiter().GetResult();

  public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
  {
    if (!await TryCommitAsync(cancellationToken).ConfigureAwait(false))
      throw new TransactionConflictException(TransactionId);
  }

  public ValueTask<bool> TryCommitAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var commit = BeginCommit(false, synchronous: false);
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
    var commit = BeginCommit(true, synchronous: false);
    return cancellationToken.CanBeCanceled ? commit.WaitAsync(cancellationToken) : commit;
  }

  static readonly Task<bool> Committed = Task.FromResult(true);
  static readonly Task<bool> Conflicted = Task.FromResult(false);

  /// <param name="durable">True to publish the commit once the domain made it durable.</param>
  /// <param name="synchronous">
  /// True if the caller blocks on the result: an ordinary commit then waits for its
  /// visibility on the calling thread.
  /// </param>
  Task<bool> BeginCommit(bool durable, bool synchronous)
  {
    EnsureActive();
    var views = TryClaimEnd(rollback: false);
    if (views == null)
    {
      EnsureActive();
      ThrowEnding();
    }
    // The running operations of the views end before the views are validated.
    foreach (var view in views)
      view.StopOperations();
    // A dropped column ended its view, so the transaction cannot commit any of its writes.
    var dropped = Array.Find(views, static view => view.Column.IsDropped);
    if (dropped != null)
    {
      RollbackClaimed();
      throw new SchemaObjectDroppedException(
          $"Column '{dropped.Column.Name}' was dropped, so transaction {TransactionId} was rolled back.");
    }
    // A failed write rolled back its view, so the other views roll back too.
    if (Array.Exists(views, static view => view.HasFailedWrite))
    {
      RollbackClaimed();
      throw new TransactionAbortedException(TransactionId);
    }
    // A write that found a newer commit stored nothing, so the transaction conflicts even if
    // that view has no writes. The disposed views roll back.
    if (Array.Exists(views, static view => view.ConflictDetected))
      return AbortConflict();
    if (!Array.Exists(views, static view => view.HasWrites))
      return CommitUnwritten(views, durable);
    // Only the views that wrote or have reads to validate take their column's commit gate.
    // The others cannot conflict, so they do not wait for the commits of their columns.
    var gated = Array.TrueForAll(views, static view => view.NeedsValidation)
        ? views
        : Array.FindAll(views, static view => view.NeedsValidation);
    if (gated.Length > 1)
      Array.Sort(gated, static (left, right) => left.Column.Id.CompareTo(right.Column.Id));

    var coordinator = Domain.Coordinator;
    var entered = 0;
    var claimed = 0;
    CommitSequencer.Slot slot = null;
    try
    {
      for (; entered < gated.Length; ++entered)
        Monitor.Enter(gated[entered].Column.CommitGate);
      for (; claimed < views.Length; ++claimed)
        views[claimed].ClaimForCommit();
      // Serializable views validate their reads even if they wrote nothing.
      if (Array.TrueForAll(gated, static view => view.Validate()))
      {
        state = TransactionState.Committing;
        slot = coordinator.BeginCommit();
        try
        {
          foreach (var view in gated)
            if (view.HasWrites)
              view.Prepare(slot.Timestamp);
        }
        catch (Exception error)
        {
          // Commits stop before the timestamp completes, so the durable timestamp never
          // reaches records the rollback could not delete.
          if (!RollbackViews(views, claimed))
            coordinator.StopCommits(error);
          _ = coordinator.Sequencer.Complete(slot);
          claimed = 0;
          throw;
        }
      }
    }
    catch
    {
      RollbackViews(views, claimed);
      RollbackClaimed();
      throw;
    }
    finally
    {
      for (var index = entered - 1; index >= 0; --index)
        Monitor.Exit(gated[index].Column.CommitGate);
    }

    if (slot == null)
    {
      RollbackViews(views, claimed);
      return AbortConflict();
    }
    if (durable)
      return CommitDurable(views, slot);
    Task visible;
    try
    {
      PublishViews(views);
    }
    finally
    {
      // The domain's snapshot moves past the commit even if a publication failed.
      visible = coordinator.Sequencer.Complete(slot);
    }
    if (!visible.IsCompleted)
    {
      if (!synchronous)
        return CompleteWhenVisible(visible, slot.Timestamp);
      // The caller blocks anyway. The completing thread signals a synchronous wait itself,
      // with no thread pool continuation.
#pragma warning disable CA1849
      visible.GetAwaiter().GetResult();
#pragma warning restore CA1849
    }
    return CompleteCommit(slot.Timestamp);
  }

  /// <summary>
  /// Commits a transaction that wrote nothing. It is equivalent to running at its snapshot,
  /// which includes every read of a serializable transaction.
  /// </summary>
  Task<bool> CommitUnwritten(ColumnView[] views, bool durable)
  {
    var claimed = 0;
    try
    {
      for (; claimed < views.Length; ++claimed)
        views[claimed].ClaimForCommit();
    }
    catch
    {
      RollbackViews(views, claimed);
      RollbackClaimed();
      throw;
    }
    PublishViews(views);
    var committed = CompleteCommit(SnapshotTimestamp);
    // A durable commit waits until what it read is durable.
    return durable ? SyncReadsAsync() : committed;
  }

  async Task<bool> SyncReadsAsync()
  {
    await Domain.Coordinator.SyncAsync().ConfigureAwait(false);
    return true;
  }

  /// <summary>
  /// Waits until the domain made the commit durable. The domain's sync thread then
  /// publishes the views, in commit order.
  /// </summary>
  async Task<bool> CommitDurable(ColumnView[] views, CommitSequencer.Slot slot)
  {
    var sequencer = Domain.Coordinator.Sequencer;
    sequencer.MarkWritten(slot);
    Task visible = null;
    try
    {
      await Domain.Coordinator.WaitDurable(slot.Timestamp, () =>
      {
        PublishViews(views);
        visible = sequencer.Complete(slot);
      }).ConfigureAwait(false);
    }
    catch (Exception error)
    {
      // The records may be on the disk, so recovery decides the outcome.
      if (visible == null)
      {
        foreach (var view in views)
          view.LeaveInDoubt();
        _ = sequencer.Complete(slot);
      }
      state = TransactionState.InDoubt;
      Close();
      throw new TransactionInDoubtException(TransactionId, error);
    }
    await visible.ConfigureAwait(false);
    return await CompleteCommit(slot.Timestamp).ConfigureAwait(false);
  }

  async Task<bool> CompleteWhenVisible(Task visible, long commitTimestamp)
  {
    // An earlier commit of the domain is still in flight; the snapshot reaches this one
    // after it, and the commit returns only then, so a later transaction reads it.
    await visible.ConfigureAwait(false);
    return await CompleteCommit(commitTimestamp).ConfigureAwait(false);
  }

  static void PublishViews(ColumnView[] views)
  {
    foreach (var view in views)
      view.Publish();
  }

  /// <summary>
  /// Rolls back the views whose end the commit claimed. Returns false if a rollback could
  /// not delete the records of its view.
  /// </summary>
  static bool RollbackViews(ColumnView[] views, int claimed)
  {
    var deleted = true;
    for (var index = 0; index < claimed; ++index)
      deleted &= views[index].RollbackClaimed();
    return deleted;
  }

  Task<bool> CompleteCommit(long commitTimestamp)
  {
    CommitTimestamp = commitTimestamp;
    state = TransactionState.Committed;
    Close();
    return Committed;
  }

  Task<bool> AbortConflict()
  {
    CommitConflict = true;
    RollbackClaimed();
    return Conflicted;
  }

  // A cluster that closes while the transaction begins may end it before it has its writer.
  protected override void ReleaseSnapshot()
  {
    if (Writer != null)
      Domain.Coordinator.Snapshots.EndWriter(Writer);
  }

  protected override void ThrowEnding() => throw new TransactionCommitInProgressException(TransactionId);

  protected override void ThrowNotActive() => throw state switch
  {
    TransactionState.Committed => (Exception)new TransactionAlreadyCommittedException(TransactionId),
    TransactionState.Committing => new TransactionCommitInProgressException(TransactionId),
    TransactionState.InDoubt => new TransactionInDoubtException(TransactionId),
    _ => new TransactionAbortedException(TransactionId),
  };

  public void Rollback() => Dispose();
  public ValueTask RollbackAsync() => DisposeAsync();
}
