namespace ZoneTree.Cluster;

// The queues only reference the transactions; their callers own and dispose them.
#pragma warning disable CA2000

/// <summary>
/// The active transaction views of a tree, for its begin sequences and counts. The tree
/// guards it with its state lock, except that read-only views join and leave
/// <see cref="SnapshotBucket"/>s without it. The domain keeps the snapshots.
/// </summary>
/// <remarks>
/// Writing views and snapshot buckets are registered under the state lock, and their begin
/// sequences never decrease, so the oldest one holds the smallest. Ended writers and empty
/// buckets leave lazily: they are dropped when they reach the front, and the writer queue
/// is rebuilt when ended writers dominate it.
/// </remarks>
internal sealed class ActiveTransactions<TKey, TValue>
{
  readonly Queue<ClusterMvccTransaction<TKey, TValue>> Writers = new();

  readonly Queue<SnapshotBucket> Buckets = new();

  int WriterCount;

  SnapshotBucket currentBucket;

  /// <summary>
  /// The bucket of the latest snapshot, read without the state lock.
  /// </summary>
  public SnapshotBucket CurrentBucket => Volatile.Read(ref currentBucket);

  public int Count
  {
    get
    {
      var count = WriterCount;
      foreach (var bucket in Buckets)
        count += Math.Max(0, bucket.Count);
      return count;
    }
  }

  public void AddWriter(ClusterMvccTransaction<TKey, TValue> transaction)
  {
    transaction.IsRegistered = true;
    Writers.Enqueue(transaction);
    ++WriterCount;
  }

  public void RemoveWriter(ClusterMvccTransaction<TKey, TValue> transaction)
  {
    transaction.IsRegistered = false;
    --WriterCount;
    while (Writers.TryPeek(out var oldest) && !oldest.IsRegistered)
      Writers.Dequeue();
    if (Writers.Count > 2 * WriterCount + 64)
    {
      var count = Writers.Count;
      for (var i = 0; i < count; ++i)
      {
        var writer = Writers.Dequeue();
        if (writer.IsRegistered)
          Writers.Enqueue(writer);
      }
    }
  }

  /// <summary>
  /// Creates the bucket of a new snapshot with the caller joined.
  /// </summary>
  public SnapshotBucket AddBucket(long snapshotTimestamp, long beginSequence)
  {
    var bucket = new SnapshotBucket(snapshotTimestamp, beginSequence);
    bucket.TryJoin();
    Buckets.Enqueue(bucket);
    Volatile.Write(ref currentBucket, bucket);
    // A long reader keeps the front bucket; drop the empty ones behind it.
    if (Buckets.Count % 64 == 0)
    {
      var count = Buckets.Count;
      for (var i = 0; i < count; ++i)
      {
        var item = Buckets.Dequeue();
        if (item == bucket || !item.TryRetire())
          Buckets.Enqueue(item);
      }
    }
    return bucket;
  }

  /// <summary>
  /// Retires the empty buckets at the front. A retired bucket cannot be joined,
  /// so the buckets left all count readers or were created after them.
  /// </summary>
  void TrimBuckets()
  {
    while (Buckets.TryPeek(out var oldest) && oldest.TryRetire())
      Buckets.Dequeue();
  }

  /// <summary>
  /// The begin sequence of the oldest active transaction.
  /// </summary>
  public long OldestBeginSequence(long defaultValue)
  {
    TrimBuckets();
    var oldest = Writers.TryPeek(out var writer) ? writer.BeginSequence : defaultValue;
    if (Buckets.TryPeek(out var bucket))
      oldest = Math.Min(oldest, bucket.BeginSequence);
    return oldest;
  }

}
#pragma warning restore CA2000
