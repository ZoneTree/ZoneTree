namespace ZoneTree.Cluster;

/// <summary>
/// The view of one column inside a domain transaction, which the domain transaction uses
/// to coordinate its views of differently typed columns.
/// </summary>
internal abstract class ColumnView : IDisposable, IColumnDependent
{
  internal Column Column;
  internal virtual bool ConflictDetected => false;
  internal virtual bool HasWrites => false;
  internal virtual bool CanCommit() => true;

  /// <summary>
  /// Rejects new operations of the view and waits for the running ones, before the
  /// domain transaction validates and commits it.
  /// </summary>
  internal abstract void StopOperations();

  internal abstract Task<bool> BeginCommit(bool durable);
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
