namespace ZoneTree.Transactional.Mvcc;

// The queues only reference the transactions; their callers own and dispose them.
#pragma warning disable CA2000

/// <summary>
/// The active transactions. The tree guards it with its state lock, except that
/// read-only transactions join and leave <see cref="SnapshotBucket"/>s without it.
/// </summary>
/// <remarks>
/// Writing transactions and snapshot buckets are created under the state lock, and
/// their snapshot timestamps and begin sequences never decrease, so the oldest one
/// holds the smallest of both. Ended writers and empty buckets leave lazily: they are
/// dropped when they reach the front, and the writer queue is rebuilt when ended
/// writers dominate it.
/// </remarks>
internal sealed class ActiveTransactions<TKey, TValue>
{
  readonly Queue<MvccTransaction<TKey, TValue>> Writers = new();

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

  public void AddWriter(MvccTransaction<TKey, TValue> transaction)
  {
    transaction.IsRegistered = true;
    Writers.Enqueue(transaction);
    ++WriterCount;
  }

  public void RemoveWriter(MvccTransaction<TKey, TValue> transaction)
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
  /// The snapshot timestamp of the oldest active transaction.
  /// </summary>
  public long OldestSnapshot(long defaultValue)
  {
    TrimBuckets();
    var oldest = OldestWriterSnapshot(defaultValue);
    if (Buckets.TryPeek(out var bucket))
      oldest = Math.Min(oldest, bucket.SnapshotTimestamp);
    return oldest;
  }

  /// <summary>
  /// The snapshot timestamp of the oldest active transaction that can write.
  /// </summary>
  public long OldestWriterSnapshot(long defaultValue)
      => Writers.TryPeek(out var writer) ? writer.SnapshotTimestamp : defaultValue;

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

  /// <summary>
  /// The snapshot timestamps in use in ascending order, followed by the given one.
  /// </summary>
  public long[] GetSnapshots(long latestSnapshot)
  {
    TrimBuckets();
    var snapshots = new List<long>(WriterCount + Buckets.Count + 1);
    foreach (var writer in Writers)
    {
      if (writer.IsRegistered)
        snapshots.Add(writer.SnapshotTimestamp);
    }
    // Buckets with no reader may still be joined while the merge runs.
    foreach (var bucket in Buckets)
      snapshots.Add(bucket.SnapshotTimestamp);
    snapshots.Add(latestSnapshot);
    snapshots.Sort();
    return snapshots.ToArray();
  }
}
#pragma warning restore CA2000
