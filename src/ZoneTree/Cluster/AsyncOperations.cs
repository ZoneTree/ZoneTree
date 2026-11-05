namespace ZoneTree.Cluster;

/// <summary>
/// Runs the async data operations of the cluster on the thread pool, so a caller can run
/// operations in parallel and decide which ones to await before starting others.
/// </summary>
internal static class AsyncOperations
{
  internal static ValueTask<T> Run<T>(Func<T> operation, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(Task.Run(operation, cancellationToken));
  }

  internal static ValueTask Run(Action operation, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(Task.Run(operation, cancellationToken));
  }
}
