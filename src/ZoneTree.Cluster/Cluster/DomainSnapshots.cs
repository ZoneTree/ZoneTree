namespace ZoneTree.Cluster;

/// <summary>
/// The snapshots of a domain's active transactions, for the columns' merges and conflict
/// cleanup.
/// </summary>
/// <remarks>
/// <para>
/// A domain transaction opens its column views lazily, so a view with an older snapshot can
/// register in a column after a view with a newer one. The columns therefore take the oldest
/// snapshots from the domain, where transactions register when they begin, in snapshot order.
/// </para>
/// <para>
/// A writer registers under the gate, together with reading its snapshot, so the queue is in
/// snapshot order; it ends without the gate, and the next holder of the gate drops it from
/// the queue. Read-only transactions join and leave <see cref="SnapshotBucket"/>s without
/// the gate. The oldest snapshots are published as volatile values that are never above the
/// true ones: registering lowers them under the gate before the transaction reads anything,
/// and only the gate raises them, after the transactions ended.
/// </para>
/// </remarks>
internal sealed class DomainSnapshots
{
  /// <summary>The registration of a writing transaction.</summary>
  internal sealed class Writer(long snapshotTimestamp, long transactionId)
  {
    internal readonly long SnapshotTimestamp = snapshotTimestamp;

    /// <summary>
    /// The id of the transaction. It is also its begin sequence: the clock value issued
    /// before the transaction read anything.
    /// </summary>
    internal readonly long TransactionId = transactionId;
    /// <summary>One once the transaction ended.</summary>
    internal int Ended;

    internal bool IsRegistered => Volatile.Read(ref Ended) == 0;

    /// <summary>The transaction, which a closing cluster rolls back.</summary>
    internal DomainTransaction Owner;
  }

  readonly CommitSequencer Sequencer;
  readonly ITimestampOracle Clock;
  // A monitor rather than a Lock, so that ending a writer can try it on every framework.
  readonly object Gate = new();
  readonly Queue<Writer> Writers = new();
  readonly Queue<SnapshotBucket> Buckets = new();
  int WriterCount;
  SnapshotBucket currentBucket;
  long oldestSnapshot = long.MaxValue;
  long oldestWriterSnapshot = long.MaxValue;
  long oldestBeginSequence = long.MaxValue;

  internal DomainSnapshots(CommitSequencer sequencer, ITimestampOracle clock)
  {
    Sequencer = sequencer;
    Clock = clock;
  }

  /// <summary>The oldest snapshot an active or future transaction can read.</summary>
  internal long OldestSnapshot => Math.Min(Volatile.Read(ref oldestSnapshot), Sequencer.SnapshotTimestamp);

  /// <summary>The oldest snapshot an active or future writing transaction can read.</summary>
  internal long OldestWriterSnapshot => Math.Min(Volatile.Read(ref oldestWriterSnapshot), Sequencer.SnapshotTimestamp);

  /// <summary>
  /// The oldest begin sequence of an active or future transaction. A rollback whose deletions
  /// were written before it is invisible to every transaction.
  /// </summary>
  internal long OldestBeginSequence
  {
    get
    {
      // The clock first: a transaction that registers after this read begins after it.
      var next = Clock.Current + 1;
      return Math.Min(Volatile.Read(ref oldestBeginSequence), next);
    }
  }

  /// <summary>The active writing and read-only transactions of the domain.</summary>
  internal int ActiveCount
  {
    get
    {
      lock (Gate)
      {
        var count = Volatile.Read(ref WriterCount);
        foreach (var bucket in Buckets)
          count += Math.Max(0, bucket.Count);
        return count;
      }
    }
  }

  /// <summary>The writing transactions that are active now.</summary>
  internal DomainTransaction[] GetActiveWriters()
  {
    lock (Gate)
      return Writers.Where(writer => writer.IsRegistered && writer.Owner != null)
          .Select(writer => writer.Owner)
          .ToArray();
  }

  /// <summary>Registers a writing transaction with its snapshot and a new id.</summary>
  internal Writer BeginWriter()
  {
    lock (Gate)
    {
      // Ended writers leave the queue here, or wherever the gate is taken next.
      var trimmed = TrimWriters();
      var writer = new Writer(Sequencer.SnapshotTimestamp, Clock.Next());
      Writers.Enqueue(writer);
      Interlocked.Increment(ref WriterCount);
      if (trimmed || oldestWriterSnapshot == long.MaxValue)
        UpdateOldest();
      return writer;
    }
  }

  /// <summary>
  /// Ends a writer without waiting for the gate. The published oldest snapshots may stay
  /// below the true ones until the gate is taken next; that only keeps versions longer.
  /// </summary>
  internal void EndWriter(Writer writer)
  {
    if (Interlocked.Exchange(ref writer.Ended, 1) != 0)
      return;
    Interlocked.Decrement(ref WriterCount);
    if (!Monitor.TryEnter(Gate))
      return;
    try
    {
      UpdateOldest();
    }
    finally
    {
      Monitor.Exit(Gate);
    }
  }

  /// <summary>
  /// Drops the ended writers at the front of the queue, and compacts the queue when ended
  /// writers pile up behind a long one. Returns true if the front changed. Called under
  /// the gate.
  /// </summary>
  bool TrimWriters()
  {
    var trimmed = false;
    while (Writers.TryPeek(out var oldest) && !oldest.IsRegistered)
    {
      Writers.Dequeue();
      trimmed = true;
    }
    if (Writers.Count > 2 * Volatile.Read(ref WriterCount) + 64)
    {
      var count = Writers.Count;
      for (var i = 0; i < count; ++i)
      {
        var item = Writers.Dequeue();
        if (item.IsRegistered)
          Writers.Enqueue(item);
      }
    }
    return trimmed;
  }

  /// <summary>Joins the read-only transactions of the latest snapshot.</summary>
  internal SnapshotBucket JoinReader()
  {
    var snapshot = Sequencer.SnapshotTimestamp;
    var bucket = Volatile.Read(ref currentBucket);
    if (bucket != null && bucket.SnapshotTimestamp == snapshot && bucket.TryJoin())
      return bucket;
    lock (Gate)
    {
      snapshot = Sequencer.SnapshotTimestamp;
      bucket = currentBucket;
      if (bucket != null && bucket.SnapshotTimestamp == snapshot && bucket.TryJoin())
        return bucket;
      bucket = new SnapshotBucket(snapshot, Clock.Current);
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
      UpdateOldest();
      return bucket;
    }
  }

  /// <summary>
  /// The snapshot timestamps in use in ascending order, followed by the latest snapshot,
  /// which stands for every snapshot that begins later.
  /// </summary>
  internal long[] GetSnapshots()
  {
    lock (Gate)
    {
      UpdateOldest();
      var snapshots = new List<long>(Writers.Count + Buckets.Count + 1);
      foreach (var writer in Writers)
      {
        if (writer.IsRegistered)
          snapshots.Add(writer.SnapshotTimestamp);
      }
      // Buckets with no reader may still be joined while a merge runs.
      foreach (var bucket in Buckets)
        snapshots.Add(bucket.SnapshotTimestamp);
      snapshots.Add(Sequencer.SnapshotTimestamp);
      snapshots.Sort();
      return snapshots.ToArray();
    }
  }

  /// <summary>
  /// Retires the empty buckets at the front and publishes the oldest snapshots.
  /// Called under the gate.
  /// </summary>
  void UpdateOldest()
  {
    TrimWriters();
    while (Buckets.TryPeek(out var oldest) && oldest.TryRetire())
      Buckets.Dequeue();
    var hasWriter = Writers.TryPeek(out var front);
    var hasReader = Buckets.TryPeek(out var bucket);
    var writer = hasWriter ? front.SnapshotTimestamp : long.MaxValue;
    var reader = hasReader ? bucket.SnapshotTimestamp : long.MaxValue;
    Volatile.Write(ref oldestWriterSnapshot, writer);
    Volatile.Write(ref oldestSnapshot, Math.Min(writer, reader));
    // Writers and buckets register in clock order, so the fronts began first.
    Volatile.Write(ref oldestBeginSequence, Math.Min(
        hasWriter ? front.TransactionId : long.MaxValue,
        hasReader ? bucket.BeginSequence : long.MaxValue));
  }
}
