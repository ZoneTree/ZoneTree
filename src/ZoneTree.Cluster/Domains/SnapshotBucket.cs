namespace ZoneTree.Cluster;

/// <summary>
/// Counts the read-only transactions of a domain that read the same snapshot, so they begin
/// and end without the domain's gate.
/// </summary>
/// <remarks>
/// <para>
/// The domain retires a bucket only by swapping a zero count for <see cref="Retired"/>, so
/// a joined reader is always counted. A reader joins with one fetch-and-add, which never
/// retries under contention: if the bucket is retired, it takes its increment back and
/// fails. The count of a retired bucket may then hold such passing increments.
/// </para>
/// </remarks>
internal sealed class SnapshotBucket
{
  /// <summary>The flag of a retired bucket, far above any reader count.</summary>
  const int Retired = 1 << 30;

  int count;

  public long SnapshotTimestamp { get; }

  /// <summary>
  /// The clock when the bucket was created. Readers that join later
  /// began later, so it is a lower bound of their begin sequences.
  /// </summary>
  public long BeginSequence { get; }

  /// <summary>The readers in the bucket; zero once it is retired.</summary>
  public int Count
  {
    get
    {
      var value = Volatile.Read(ref count);
      return (value & Retired) != 0 ? 0 : value;
    }
  }

  public SnapshotBucket(long snapshotTimestamp, long beginSequence)
  {
    SnapshotTimestamp = snapshotTimestamp;
    BeginSequence = beginSequence;
  }

  public bool TryJoin()
  {
    if ((Interlocked.Increment(ref count) & Retired) == 0)
      return true;
    Interlocked.Decrement(ref count);
    return false;
  }

  public void Leave() => Interlocked.Decrement(ref count);

  /// <summary>
  /// Retires the bucket if no reader is in it. Returns true if the bucket is retired.
  /// </summary>
  public bool TryRetire()
  {
    var observed = Interlocked.CompareExchange(ref count, Retired, 0);
    return observed == 0 || (observed & Retired) != 0;
  }
}
