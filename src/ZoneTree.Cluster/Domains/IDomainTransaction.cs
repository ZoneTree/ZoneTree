namespace ZoneTree.Cluster;

/// <summary>An atomic transaction over the columns of one domain.</summary>
/// <remarks>
/// Commit and rollback apply to all columns accessed by this transaction.
/// Disposing an active transaction rolls it back.
/// </remarks>
public interface IDomainTransaction : IDomainReadTransaction, IClusterWriteTransaction
{
  /// <summary>
  /// Gets a writable column view within this transaction.
  /// </summary>
  /// <exception cref="ArgumentException">The column belongs to another domain.</exception>
  new IWriteColumn<TKey, TValue> GetColumn<TKey, TValue>(
      ICoordinatedColumn<TKey, TValue> column);
}
