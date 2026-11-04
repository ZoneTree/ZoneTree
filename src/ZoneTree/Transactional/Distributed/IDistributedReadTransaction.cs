namespace ZoneTree.Transactional.Distributed;

/// <summary>A consistent snapshot shared by all columns read by a transaction.</summary>
/// <remarks>
/// Dispose it to release snapshot protection and all open iterators. Column views
/// are borrowed from this transaction and cannot be used after it ends.
/// Beginning a transaction must not create a transaction in every column:
/// column views are acquired lazily while snapshot protection is database-wide.
/// </remarks>
public interface IDistributedReadTransaction : IDisposable
{
  /// <summary>
  /// The database commit timestamp of this snapshot, not a wall-clock time.
  /// All column views use this timestamp, regardless of when they are acquired.
  /// </summary>
  long SnapshotTimestamp { get; }

  /// <summary>
  /// Binds a typed column to this transaction. Repeated calls return the same view.
  /// Cache the view for a loop; point operations need no name lookup or type erasure.
  /// </summary>
  /// <exception cref="ArgumentException">The handle belongs to another database.</exception>
  /// <remarks>Acquiring a view after the transaction ends is rejected.</remarks>
  IDistributedReadColumn<TKey, TValue> GetColumn<TKey, TValue>(
      IDistributedColumn<TKey, TValue> column);
}
