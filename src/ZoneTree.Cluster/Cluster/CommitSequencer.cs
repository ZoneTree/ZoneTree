namespace ZoneTree.Cluster;

/// <summary>
/// Orders the commits of a domain: issues their commit timestamps and moves the domain's
/// snapshot timestamp in commit order.
/// </summary>
/// <remarks>
/// <para>
/// Commits register when they take their timestamp, under the gate, so the in-flight queue is
/// in timestamp order. A commit becomes visible, in every column at once, when the snapshot
/// timestamp reaches it. That happens only after it and every earlier commit completed, so a
/// snapshot includes every commit at or below its timestamp, in every column.
/// </para>
/// <para>
/// The written timestamp is the highest one at or below which every commit has written its
/// commit records. A domain sync that starts afterward makes all of them durable.
/// </para>
/// </remarks>
internal sealed class CommitSequencer
{
  /// <summary>A registered commit.</summary>
  internal sealed class Slot
  {
    internal long Timestamp;
    internal bool IsWritten;
    internal bool IsDone;

    /// <summary>Completes once the snapshot reaches the commit; null while nobody waits.</summary>
    internal TaskCompletionSource Visible;
  }

  readonly Lock Gate = new();
  readonly Queue<Slot> InFlight = new();
  long snapshotTimestamp;
  long LastDone;
  bool IsClosed;

  internal CommitSequencer(long start)
  {
    snapshotTimestamp = start;
    LastDone = start;
  }

  /// <summary>The timestamp new snapshots read at. It never decreases.</summary>
  internal long SnapshotTimestamp => Volatile.Read(ref snapshotTimestamp);

  /// <summary>Takes the timestamp of a commit and registers the commit.</summary>
  internal Slot Begin(ITimestampOracle clock)
  {
    var slot = new Slot();
    lock (Gate)
    {
      ObjectDisposedException.ThrowIf(IsClosed, this);
      slot.Timestamp = clock.Next();
      InFlight.Enqueue(slot);
    }
    return slot;
  }

  /// <summary>Records that a commit wrote its commit records in every column.</summary>
  internal void MarkWritten(Slot slot)
  {
    lock (Gate)
      slot.IsWritten = true;
  }

  /// <summary>
  /// Ends a commit that is published in every column, or abandoned after its records were
  /// rolled back, and moves the snapshot timestamp past the completed commits.
  /// </summary>
  /// <returns>
  /// A task that completes once the snapshot reaches the commit. It is complete at once,
  /// unless an earlier commit is still in flight.
  /// </returns>
  internal Task Complete(Slot slot)
  {
    List<TaskCompletionSource> visible = null;
    Task wait = Task.CompletedTask;
    lock (Gate)
    {
      slot.IsWritten = slot.IsDone = true;
      while (InFlight.TryPeek(out var head) && head.IsDone)
      {
        InFlight.Dequeue();
        LastDone = head.Timestamp;
        if (head.Visible != null)
          (visible ??= []).Add(head.Visible);
      }
      // Values between commits are transaction ids, so any of them can be a snapshot.
      var snapshot = InFlight.TryPeek(out var next) ? next.Timestamp - 1 : LastDone;
      if (snapshot > snapshotTimestamp)
        Volatile.Write(ref snapshotTimestamp, snapshot);
      if (slot.Timestamp > snapshot)
      {
        slot.Visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wait = slot.Visible.Task;
      }
    }
    if (visible != null)
      foreach (var waiter in visible)
        waiter.TrySetResult();
    return wait;
  }

  /// <summary>Refuses new commits.</summary>
  internal void Close()
  {
    lock (Gate)
      IsClosed = true;
  }

  /// <summary>
  /// The written timestamp, and whether no commit is in flight, so every commit at or below
  /// it is complete.
  /// </summary>
  internal (long WrittenTimestamp, bool IsIdle) GetProgress()
  {
    lock (Gate)
      return (GetWrittenTimestamp(), InFlight.Count == 0);
  }

  /// <summary>
  /// The highest timestamp at or below which every commit has written its commit records.
  /// </summary>
  internal long WrittenTimestamp
  {
    get
    {
      lock (Gate)
        return GetWrittenTimestamp();
    }
  }

  /// <summary>Called under the gate.</summary>
  long GetWrittenTimestamp()
  {
    var written = snapshotTimestamp;
    foreach (var slot in InFlight)
    {
      if (!slot.IsWritten)
        return Math.Max(written, slot.Timestamp - 1);
      written = slot.Timestamp;
    }
    return written;
  }
}
