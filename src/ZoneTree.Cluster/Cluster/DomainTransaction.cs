using System.Runtime.ExceptionServices;
using ZoneTree.Transactional;
using ZoneTree.Exceptions;

namespace ZoneTree.Cluster;

/// <summary>
/// A writing transaction of a domain. Its writes in every column of the domain commit
/// together, at one commit timestamp, or not at all.
/// </summary>
/// <remarks>
/// <para>
/// The commit claims the views, then takes the commit gates of the columns that need
/// validation in column id order, validates those views, takes one timestamp from the
/// domain's sequencer and registers the written keys. After the gates it writes the commit
/// record of every view that wrote, then publishes the views. Readers see the
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
    writer.Owner = this;
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

  /// <summary>The spins a blocked commit makes before it waits for its visibility.</summary>
  const int VisibilitySpinCount = 64;

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
    // The views are claimed, their operations stopped and their keys sorted before any
    // commit gate is taken, so the gates cover only validation and key registration.
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
    var conflict = false;
    CommitSequencer.Slot slot = null;
    Exception failure = null;
    try
    {
      entered = EnterGates(gated);
      // Serializable views validate their reads even if they wrote nothing.
      if (!Array.TrueForAll(gated, static view => view.Validate()))
        conflict = true;
      else
      {
        state = TransactionState.Committing;
        slot = coordinator.BeginCommit();
        foreach (var view in gated)
          if (view.HasWrites)
            view.Prepare(slot.Timestamp);
      }
    }
    catch (Exception error)
    {
      failure = error;
    }
    finally
    {
      for (var index = entered - 1; index >= 0; --index)
        Monitor.Exit(gated[index].Column.CommitGate);
    }
    // The commit records are written without the gates: a write can rotate a segment, which
    // syncs the disk. The durable timestamp cannot reach the commit before they are written.
    if (failure == null && !conflict)
    {
      try
      {
        foreach (var view in gated)
          if (view.HasWrites)
            view.WriteCommitRecord();
      }
      catch (Exception error)
      {
        failure = error;
      }
    }
    if (failure != null)
    {
      // Commits stop before the timestamp completes, so the durable timestamp never
      // reaches records the rollback could not delete.
      if (!RollbackViews(views, views.Length) && slot != null)
        coordinator.StopCommits(failure);
      if (slot != null)
        _ = coordinator.Sequencer.Complete(slot);
      RollbackClaimed();
      ExceptionDispatchInfo.Throw(failure);
    }
    if (conflict)
    {
      RollbackViews(views, views.Length);
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
      // The earlier commits are usually writing their last records, so a short spin sees
      // them complete without a kernel wait and wake-up. A long wait still blocks; the
      // completing thread signals it itself, with no thread pool continuation.
      var spin = new SpinWait();
      while (!visible.IsCompleted && spin.Count < VisibilitySpinCount)
        spin.SpinOnce(sleep1Threshold: -1);
#pragma warning disable CA1849
      visible.GetAwaiter().GetResult();
#pragma warning restore CA1849
    }
    return CompleteCommit(slot.Timestamp);
  }

  /// <summary>
  /// Takes the commit gates of the views. A transaction never waits for a gate while it holds
  /// another: when a gate is taken, it releases the ones it holds, waits for that gate alone,
  /// and tries again. Commits that share columns then do not queue behind partly held gate
  /// sets, and commits on other columns still run in parallel. Returns the gates taken.
  /// </summary>
  static int EnterGates(ColumnView[] gated)
  {
    if (gated.Length == 1)
    {
      Monitor.Enter(gated[0].Column.CommitGate);
      return 1;
    }
    var first = 0;
    while (true)
    {
      // The gate that was busy, taken while holding nothing, then the others without waiting.
      Monitor.Enter(gated[first].Column.CommitGate);
      var busy = -1;
      for (var index = 0; index < gated.Length; ++index)
      {
        if (index != first && !Monitor.TryEnter(gated[index].Column.CommitGate))
        {
          busy = index;
          break;
        }
      }
      if (busy < 0)
        return gated.Length;
      for (var index = busy - 1; index >= 0; --index)
        if (index != first)
          Monitor.Exit(gated[index].Column.CommitGate);
      Monitor.Exit(gated[first].Column.CommitGate);
      first = busy;
    }
  }

  /// <summary>
  /// Commits a transaction that wrote nothing. It is equivalent to running at its snapshot,
  /// which includes every read of a serializable transaction.
  /// </summary>
  Task<bool> CommitUnwritten(ColumnView[] views, bool durable)
  {
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
