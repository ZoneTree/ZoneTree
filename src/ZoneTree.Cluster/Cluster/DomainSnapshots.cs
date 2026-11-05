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
/// snapshot order. Read-only transactions join and leave <see cref="SnapshotBucket"/>s without
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
    internal readonly long TransactionId = transactionId;
    internal bool IsRegistered = true;
  }

  readonly CommitSequencer Sequencer;
  readonly ITimestampOracle Clock;
  readonly Lock Gate = new();
  readonly Queue<Writer> Writers = new();
  readonly Queue<SnapshotBucket> Buckets = new();
  int WriterCount;
  SnapshotBucket currentBucket;
  long oldestSnapshot = long.MaxValue;
  long oldestWriterSnapshot = long.MaxValue;

  internal DomainSnapshots(CommitSequencer sequencer, ITimestampOracle clock)
  {
    Sequencer = sequencer;
    Clock = clock;
  }

  /// <summary>The oldest snapshot an active or future transaction can read.</summary>
  internal long OldestSnapshot => Math.Min(Volatile.Read(ref oldestSnapshot), Sequencer.SnapshotTimestamp);

  /// <summary>The oldest snapshot an active or future writing transaction can read.</summary>
  internal long OldestWriterSnapshot => Math.Min(Volatile.Read(ref oldestWriterSnapshot), Sequencer.SnapshotTimestamp);

  /// <summary>Registers a writing transaction with its snapshot and a new id.</summary>
  internal Writer BeginWriter()
  {
    lock (Gate)
    {
      var writer = new Writer(Sequencer.SnapshotTimestamp, Clock.Next());
      Writers.Enqueue(writer);
      ++WriterCount;
      if (oldestWriterSnapshot == long.MaxValue)
        UpdateOldest();
      return writer;
    }
  }

  internal void EndWriter(Writer writer)
  {
    lock (Gate)
    {
      if (!writer.IsRegistered)
        return;
      writer.IsRegistered = false;
      --WriterCount;
      while (Writers.TryPeek(out var oldest) && !oldest.IsRegistered)
        Writers.Dequeue();
      if (Writers.Count > 2 * WriterCount + 64)
      {
        var count = Writers.Count;
        for (var i = 0; i < count; ++i)
        {
          var item = Writers.Dequeue();
          if (item.IsRegistered)
            Writers.Enqueue(item);
        }
      }
      UpdateOldest();
    }
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
      var snapshots = new List<long>(WriterCount + Buckets.Count + 1);
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
    while (Buckets.TryPeek(out var oldest) && oldest.TryRetire())
      Buckets.Dequeue();
    var writer = Writers.TryPeek(out var front) ? front.SnapshotTimestamp : long.MaxValue;
    var reader = Buckets.TryPeek(out var bucket) ? bucket.SnapshotTimestamp : long.MaxValue;
    Volatile.Write(ref oldestWriterSnapshot, writer);
    Volatile.Write(ref oldestSnapshot, Math.Min(writer, reader));
  }
}
