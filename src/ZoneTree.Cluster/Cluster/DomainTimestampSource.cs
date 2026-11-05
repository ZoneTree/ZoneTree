using ZoneTree.Exceptions;

namespace ZoneTree.Cluster;

/// <summary>
/// Issues a domain's transaction ids and commit timestamps from one clock, and keeps the
/// domain's snapshot timestamp.
/// </summary>
/// <remarks>
/// <para>
/// One atomic clock gives ids and timestamps a single total order. A value is issued only
/// when it is at or below the reserved bound, which the catalog holds durably. After a
/// restart the clock starts at the last reserved bound, the restart epoch, so values never
/// repeat and never go backward.
/// </para>
/// <para>
/// Once half of the reserved batch is used, the cluster's reservation worker reserves the
/// next one on its own thread. Callers never perform the reservation; a caller waits only
/// if it reaches the bound before the next batch is durable.
/// </para>
/// </remarks>
internal sealed class DomainTimestampSource
{
  const long HalfBatch = ClusterCatalog.TimestampBatch / 2;

  // After a failed reservation, callers that are still within the bound ask again only after
  // this delay, so a failing catalog is not retried on every call.
  const long RetryDelayMilliseconds = 1000;

  readonly TimestampReservationWorker Reservations;
  readonly object Gate = new();
  long Clock;
  long snapshotTimestamp;
  long reservedBound;
  // The fields below are written under the gate.
  volatile bool requested;
  long retryAt;
  Exception failure;
  long failureCount;

  internal long DomainId { get; }

  internal DomainTimestampSource(TimestampReservationWorker reservations, long domainId, long start, long reservedBound)
  {
    Reservations = reservations;
    DomainId = domainId;
    Clock = start;
    snapshotTimestamp = start;
    this.reservedBound = reservedBound;
  }

  /// <summary>
  /// The timestamp a new snapshot of the domain reads at: every commit at or below it is
  /// included. A commit gets its timestamp only after it is visible, so the highest commit
  /// timestamp is such a point. It never decreases; after a restart it starts at the restart
  /// epoch, which is above every earlier commit.
  /// </summary>
  internal long SnapshotTimestamp => Volatile.Read(ref snapshotTimestamp);

  /// <summary>The bound that the next reservation reserves.</summary>
  internal long NextBound => Volatile.Read(ref reservedBound) + ClusterCatalog.TimestampBatch;

  /// <summary>Issues the next transaction id or commit timestamp.</summary>
  internal long Next()
  {
    var value = Interlocked.Increment(ref Clock);
    if (value > Volatile.Read(ref reservedBound) - HalfBatch)
      EnsureReserved(value);
    return value;
  }

  /// <summary>Issues the timestamp of a visible commit and advances the snapshot timestamp.</summary>
  internal long Commit()
  {
    var timestamp = Next();
    var current = Volatile.Read(ref snapshotTimestamp);
    while (current < timestamp)
    {
      var observed = Interlocked.CompareExchange(ref snapshotTimestamp, timestamp, current);
      if (observed == current)
        break;
      current = observed;
    }
    return timestamp;
  }

  void EnsureReserved(long value)
  {
    // Within the bound, with the next batch requested or a retry not due yet, a caller
    // continues without taking the gate.
    if (value <= Volatile.Read(ref reservedBound) &&
        (requested || Environment.TickCount64 < Volatile.Read(ref retryAt)))
      return;
    lock (Gate)
    {
      var failures = failureCount;
      while (true)
      {
        var bound = reservedBound;
        if (value <= bound - HalfBatch)
          return;
        var needed = value > bound;
        if (needed && failureCount != failures)
          throw new ZoneTreeException(
              $"Domain {DomainId} could not reserve transaction ids and commit timestamps.", failure);
        if (!requested && (needed || Environment.TickCount64 >= retryAt))
        {
          requested = true;
          // A stopped worker reports the failure at once, which the next pass sees.
          Reservations.Request(this);
          continue;
        }
        if (!needed)
          return;
        Monitor.Wait(Gate);
      }
    }
  }

  /// <summary>Called by the reservation worker once the next bound is durable.</summary>
  internal void OnReserved(long bound)
  {
    lock (Gate)
    {
      Volatile.Write(ref reservedBound, Math.Max(reservedBound, bound));
      requested = false;
      Volatile.Write(ref retryAt, 0);
      Monitor.PulseAll(Gate);
    }
  }

  /// <summary>Called by the reservation worker when a reservation failed.</summary>
  internal void OnReservationFailed(Exception error)
  {
    lock (Gate)
    {
      failure = error;
      ++failureCount;
      requested = false;
      Volatile.Write(ref retryAt, Environment.TickCount64 + RetryDelayMilliseconds);
      Monitor.PulseAll(Gate);
    }
  }
}
