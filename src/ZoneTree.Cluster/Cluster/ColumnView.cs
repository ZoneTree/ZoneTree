namespace ZoneTree.Cluster;

/// <summary>
/// The view of one column inside a domain transaction, which the domain transaction uses
/// to coordinate its views of differently typed columns.
/// </summary>
/// <remarks>
/// A writing domain transaction commits its views together, under their columns' commit
/// gates: it claims each view, validates every view, takes one commit timestamp, prepares
/// every view that wrote, and then publishes them all. Read-only views never commit.
/// </remarks>
internal abstract class ColumnView : IDisposable, IColumnDependent
{
  internal Column Column;
  internal virtual bool ConflictDetected => false;
  internal virtual bool HasWrites => false;

  /// <summary>
  /// True if the view wrote or has reads to validate, so its commit takes its column's commit
  /// gate. Stable once the view's operations stopped.
  /// </summary>
  internal virtual bool NeedsValidation => false;

  /// <summary>True if a write of the view failed, so the transaction must not commit.</summary>
  internal virtual bool HasFailedWrite => false;

  /// <summary>
  /// Rejects new operations of the view and waits for the running ones, before the
  /// domain transaction validates and commits it.
  /// </summary>
  internal abstract void StopOperations();

  /// <summary>Takes the end of the view for the commit of its domain transaction.</summary>
  internal abstract void ClaimForCommit();

  /// <summary>
  /// Checks the view against the commits after its snapshot. Called under the column's
  /// commit gate.
  /// </summary>
  internal abstract bool Validate();

  /// <summary>
  /// Registers the view's written keys at the commit timestamp and writes its commit record.
  /// Called under the column's commit gate, for views that wrote.
  /// </summary>
  internal abstract void Prepare(long commitTimestamp);

  /// <summary>Publishes the committed view; readers see it once the domain's snapshot reaches it.</summary>
  internal abstract void Publish();

  /// <summary>Leaves the view in doubt after the domain failed to make its commit durable.</summary>
  internal abstract void LeaveInDoubt();

  /// <summary>
  /// Rolls back a view whose end the commit claimed. Returns false if the deletion of its
  /// writes failed, so its records may remain.
  /// </summary>
  internal abstract bool RollbackClaimed();

  public abstract void Dispose();

  /// <summary>
  /// Ends the view of a dropped column: stops its operations and rolls it back. The domain
  /// transaction then cannot commit, and reports the dropped column.
  /// </summary>
  void IColumnDependent.OnColumnDropped()
  {
    StopOperations();
    Dispose();
  }

  /// <summary>
  /// Runs an async operation of this view on the thread pool. The view is thread-safe,
  /// so its operations, and those of other views, run in parallel.
  /// </summary>
  internal static ValueTask<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
      => AsyncOperations.Run(operation, cancellationToken);

  internal static ValueTask RunAsync(Action operation, CancellationToken cancellationToken)
      => AsyncOperations.Run(operation, cancellationToken);
}
