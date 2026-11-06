using ZoneTree.Exceptions;

namespace ZoneTree.Cluster;

/// <summary>A point in a domain's commit order: a commit timestamp and its sequence number.</summary>
internal readonly record struct CommitPoint(long Timestamp, long Sequence);

/// <summary>
/// Orders the commits of a domain: issues their commit timestamps and sequence numbers, and
/// moves the domain's snapshot timestamp in commit order.
/// </summary>
/// <remarks>
/// <para>
/// Commits register when they take their timestamp, under the gate, so the in-flight queue is
/// in timestamp order. A commit becomes visible, in every column at once, when the snapshot
/// timestamp reaches it. That happens only after it and every earlier commit completed, so a
/// snapshot includes every commit at or below its timestamp, in every column.
/// </para>
/// <para>
/// Each commit also takes the next sequence number, so the numbers have no gaps and follow
/// the timestamps. Recovery cuts at the first number it cannot find complete. A commit that
/// fails after it took its number therefore stops the sequencer: it never completes, so no
/// later commit becomes visible, and every one waiting fails.
/// </para>
/// <para>
/// The written point is the highest one at or below which every commit has written its
/// commit records. A domain sync that starts afterward makes all of them durable.
/// </para>
/// </remarks>
internal sealed class CommitSequencer
{
  /// <summary>A registered commit.</summary>
  internal sealed class Slot
  {
    internal long Timestamp;
    internal long Sequence;
    internal bool IsWritten;
    internal bool IsDone;

    /// <summary>Completes once the snapshot reaches the commit; null while nobody waits.</summary>
    internal TaskCompletionSource Visible;
  }

  readonly Lock Gate = new();
  readonly Queue<Slot> InFlight = new();
  long snapshotTimestamp;
  long LastDone;
  long LastSequence;
  bool IsClosed;

  /// <summary>The failure that stopped the sequencer, or null.</summary>
  Exception Failure;

  /// <summary>The result of every commit that waits behind a failed one.</summary>
  Task Stopped;

  /// <summary>Starts at a point every earlier commit, of any process, is at or below.</summary>
  internal CommitSequencer(long start)
  {
    snapshotTimestamp = start;
    LastDone = start;
    LastSequence = start;
  }

  /// <summary>The timestamp new snapshots read at. It never decreases.</summary>
  internal long SnapshotTimestamp => Volatile.Read(ref snapshotTimestamp);

  /// <summary>Takes the timestamp and the sequence number of a commit and registers the commit.</summary>
  internal Slot Begin(ITimestampOracle clock)
  {
    var slot = new Slot();
    lock (Gate)
    {
      ObjectDisposedException.ThrowIf(IsClosed, this);
      if (Failure != null)
        throw new TransactionalTreeCommitsStoppedException(Failure);
      slot.Timestamp = clock.Next();
      slot.Sequence = ++LastSequence;
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
  /// Ends a commit that is published in every column, or left in doubt after it wrote its
  /// commit records, and moves the snapshot timestamp past the completed commits.
  /// </summary>
  /// <returns>
  /// A task that completes once the snapshot reaches the commit. It is complete at once,
  /// unless an earlier commit is still in flight. It fails if an earlier commit stopped the
  /// sequencer.
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
        if (Stopped != null)
          wait = Stopped;
        else
        {
          slot.Visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          wait = slot.Visible.Task;
        }
      }
    }
    if (visible != null)
      foreach (var waiter in visible)
        waiter.TrySetResult();
    return wait;
  }

  /// <summary>
  /// Stops the sequencer at a commit that failed after it took its sequence number. The
  /// commit never completes, so the snapshot stays below it, and the later commits that wait
  /// to become visible fail.
  /// </summary>
  internal void Stop(Slot slot, Exception error)
  {
    List<TaskCompletionSource> failed = null;
    lock (Gate)
    {
      if (Failure != null)
        return;
      Failure = error;
      var stopped = new TaskCompletionSource();
      stopped.SetException(new TransactionalTreeCommitsStoppedException(error));
      Stopped = stopped.Task;
      // Every commit that waits for its own completion fails the same way.
      _ = Stopped.Exception;
      foreach (var waiting in InFlight)
        if (waiting.Visible != null && waiting.Timestamp > slot.Timestamp)
          (failed ??= []).Add(waiting.Visible);
    }
    if (failed != null)
      foreach (var waiter in failed)
        waiter.TrySetException(new TransactionalTreeCommitsStoppedException(error));
  }

  /// <summary>Refuses new commits.</summary>
  internal void Close()
  {
    lock (Gate)
      IsClosed = true;
  }

  /// <summary>
  /// The written point, and whether no commit is in flight, so every commit at or below it
  /// is complete.
  /// </summary>
  internal (CommitPoint Written, bool IsIdle) GetProgress()
  {
    lock (Gate)
      return (GetWritten(), InFlight.Count == 0);
  }

  /// <summary>Whether every commit at or below the timestamp has written its commit records.</summary>
  internal bool IsWrittenThrough(long timestamp)
  {
    lock (Gate)
    {
      foreach (var slot in InFlight)
      {
        if (slot.Timestamp > timestamp)
          return true;
        if (!slot.IsWritten)
          return false;
      }
      return true;
    }
  }

  /// <summary>
  /// The highest point at or below which every commit has written its commit records.
  /// </summary>
  internal CommitPoint Written
  {
    get
    {
      lock (Gate)
        return GetWritten();
    }
  }

  /// <summary>Called under the gate.</summary>
  CommitPoint GetWritten()
  {
    var timestamp = snapshotTimestamp;
    var sequence = InFlight.TryPeek(out var head) ? head.Sequence - 1 : LastSequence;
    foreach (var slot in InFlight)
    {
      if (!slot.IsWritten)
        return new(Math.Max(timestamp, slot.Timestamp - 1), sequence);
      timestamp = slot.Timestamp;
      sequence = slot.Sequence;
    }
    return new(timestamp, sequence);
  }
}
