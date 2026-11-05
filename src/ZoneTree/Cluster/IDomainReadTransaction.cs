namespace ZoneTree.Cluster;

/// <summary>A consistent snapshot shared by the domain columns read by a transaction.</summary>
/// <remarks>
/// Column views, and the operations of each view, may be used concurrently. A commit or
/// rollback waits for the running operations, and one that starts while the transaction
/// ends throws. Column views and iterators cannot be used after the transaction ends.
/// </remarks>
public interface IDomainReadTransaction : IDisposable, IAsyncDisposable
{
  /// <summary>
  /// The snapshot timestamp shared by all column views.
  /// </summary>
  long SnapshotTimestamp { get; }

  /// <summary>
  /// Gets a typed column view within this transaction.
  /// </summary>
  /// <exception cref="ArgumentException">The handle belongs to another cluster.</exception>
  /// <remarks>Acquiring a view after the transaction ends is rejected.</remarks>
  IReadColumn<TKey, TValue> GetColumn<TKey, TValue>(
      ICoordinatedColumn<TKey, TValue> column);
}
