using ZoneTree.Comparers;

namespace ZoneTree.Cluster;

/// <summary>
/// A read-only transaction of <see cref="ClusterMvccZoneTree{TKey, TValue}"/>.
/// It reads the snapshot of its bucket and keeps no read set.
/// </summary>
internal sealed class ClusterMvccReadTransaction<TKey, TValue> : ClusterMvccReader<TKey, TValue>
{
  readonly SnapshotBucket Bucket;

  bool isDisposed;

  public ClusterMvccReadTransaction(
      ClusterMvccZoneTree<TKey, TValue> tree,
      IRefComparer<TKey> comparer,
      SnapshotBucket bucket)
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
