using ZoneTree.Comparers;

namespace ZoneTree.Transactional.Mvcc;

/// <summary>
/// A read-only transaction of <see cref="MvccZoneTree{TKey, TValue}"/>.
/// It reads the snapshot of its bucket and keeps no read set.
/// </summary>
internal sealed class MvccReadTransaction<TKey, TValue> : MvccReader<TKey, TValue>
{
  readonly SnapshotBucket Bucket;

  int isDisposed;

  public MvccReadTransaction(
      MvccZoneTree<TKey, TValue> tree,
      IRefComparer<TKey> comparer,
      SnapshotBucket bucket)
      : base(tree, comparer, 0, bucket.SnapshotTimestamp)
  {
    Bucket = bucket;
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
    // Leaves without the state lock; a later writer or merge
    // prunes what the snapshot kept.
    Bucket.Leave();
  }
}
