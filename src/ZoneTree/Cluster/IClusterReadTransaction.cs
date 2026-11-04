namespace ZoneTree.Cluster;

/// <summary>A consistent snapshot shared by all columns read by a transaction.</summary>
/// <remarks>
/// Different column views may be used concurrently; each individual view is sequential.
/// Finish outstanding operations before ending the transaction. Column views and
/// iterators cannot be used after the transaction ends.
/// </remarks>
public interface IClusterReadTransaction : IDisposable, IAsyncDisposable
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
      IColumn<TKey, TValue> column);
}
