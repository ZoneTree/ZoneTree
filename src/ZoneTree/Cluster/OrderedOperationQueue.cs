namespace ZoneTree.Cluster;

/// <summary>
/// Runs async operations on the thread pool, one at a time and in call order.
/// A transaction's state is not thread-safe, so its async operations share one queue,
/// while the operations of different transactions or column views run in parallel.
/// </summary>
internal sealed class OrderedOperationQueue
{
  readonly Lock OperationLock = new();

  Task LastOperation = Task.CompletedTask;

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
