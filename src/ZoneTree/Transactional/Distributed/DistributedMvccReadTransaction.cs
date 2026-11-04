using ZoneTree.Comparers;

namespace ZoneTree.Transactional.Distributed;

/// <summary>
/// A read-only transaction of <see cref="DistributedMvccZoneTree{TKey, TValue}"/>.
/// It reads the snapshot of its bucket and keeps no read set.
/// </summary>
internal sealed class DistributedMvccReadTransaction<TKey, TValue> : DistributedMvccReader<TKey, TValue>
{
  readonly DistributedSnapshotBucket Bucket;

  bool isDisposed;

  public DistributedMvccReadTransaction(
      DistributedMvccZoneTree<TKey, TValue> tree,
      IRefComparer<TKey> comparer,
      DistributedSnapshotBucket bucket)
      : base(tree, comparer, 0, bucket.SnapshotTimestamp)
  {
    Bucket = bucket;
  }

  internal override void EnsureActive()
  {
    ObjectDisposedException.ThrowIf(isDisposed, this);
  }

  public override void Dispose()
  {
    if (isDisposed)
      return;
    isDisposed = true;
    ReleaseReaders();
    // Leaves without the state lock; a later writer or merge
    // prunes what the snapshot kept.
    Bucket.Leave();
  }
}
