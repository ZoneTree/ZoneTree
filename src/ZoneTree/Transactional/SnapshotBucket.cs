namespace ZoneTree.Transactional;

/// <summary>
/// Counts the read-only transactions that read the same snapshot, so they begin and
/// end without the tree state lock.
/// </summary>
/// <remarks>
/// The count is -1 once the tree retired the bucket. A reader joins with a
/// compare-and-swap that fails on a retired bucket, and the tree retires a bucket
/// only by swapping a zero count for -1, so a joined reader is always counted.
/// </remarks>
internal sealed class SnapshotBucket
{
  int count;

  public long SnapshotTimestamp { get; }

  /// <summary>
  /// The clock when the bucket was created. Readers that join later
  /// began later, so it is a lower bound of their begin sequences.
  /// </summary>
  public long BeginSequence { get; }

  public int Count => Volatile.Read(ref count);

  public SnapshotBucket(long snapshotTimestamp, long beginSequence)
  {
    SnapshotTimestamp = snapshotTimestamp;
    BeginSequence = beginSequence;
  }

  public bool TryJoin()
  {
    var current = Volatile.Read(ref count);
    while (current >= 0)
    {
      var observed = Interlocked.CompareExchange(ref count, current + 1, current);
      if (observed == current)
        return true;
      current = observed;
    }
    return false;
  }

  public void Leave() => Interlocked.Decrement(ref count);

  /// <summary>
  /// Retires the bucket if no reader is in it.
  /// </summary>
  public bool TryRetire() => Interlocked.CompareExchange(ref count, -1, 0) == 0;
}
