namespace ZoneTree.Cluster;

internal abstract class ColumnView : IDisposable
{
  internal Column Column;
  internal virtual bool ConflictDetected => false;
  internal virtual bool HasWrites => false;
  internal virtual bool CanCommit() => true;
  internal abstract Task<bool> BeginCommit(bool durable);
  public abstract void Dispose();

  readonly Lock OperationLock = new();

  Task LastOperation = Task.CompletedTask;

  /// <summary>
  /// Runs an async operation of this view on the thread pool, so a caller can work on
  /// the views of different columns in parallel. The state of a view is not thread-safe,
  /// so its own operations run one at a time, in call order.
  /// </summary>
  internal ValueTask<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    Task<T> task;
    lock (OperationLock)
    {
      // The continuation runs even if the previous operation failed.
      task = LastOperation.ContinueWith(_ =>
      {
        cancellationToken.ThrowIfCancellationRequested();
        return operation();
      }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
      LastOperation = task;
    }
    return new(task);
  }

  internal ValueTask RunAsync(Action operation, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    Task task;
    lock (OperationLock)
    {
      task = LastOperation.ContinueWith(_ =>
      {
        cancellationToken.ThrowIfCancellationRequested();
        operation();
      }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
      LastOperation = task;
    }
    return new(task);
  }
}
