using ZoneTree.Comparers;

namespace ZoneTree.Cluster;

/// <summary>
/// The view of a read-only domain transaction in the tree of a coordinated column. It reads
/// the snapshot of its domain transaction and keeps no read set. The domain registers the
/// snapshot, so the view registers nothing in the tree.
/// </summary>
internal sealed class ClusterMvccReadTransaction<TKey, TValue> : ClusterMvccReader<TKey, TValue>
{
  int isDisposed;

  public ClusterMvccReadTransaction(
      ClusterMvccZoneTree<TKey, TValue> tree,
      IRefComparer<TKey> comparer,
      long snapshotTimestamp)
      : base(tree, comparer, 0, snapshotTimestamp)
  {
  }

  protected override bool IsActive => Volatile.Read(ref isDisposed) == 0;

  internal override void EnsureActive()
  {
    ObjectDisposedException.ThrowIf(!IsActive, this);
  }

  protected override void ThrowEnding() => ObjectDisposedException.ThrowIf(true, this);

  public override void Dispose()
  {
    if (Interlocked.Exchange(ref isDisposed, 1) != 0)
      return;
    StopOperations();
    ReleaseReaders();
  }
}
