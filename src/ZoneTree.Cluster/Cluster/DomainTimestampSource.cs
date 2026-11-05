namespace ZoneTree.Cluster;

/// <summary>
/// Issues a domain's transaction ids and commit timestamps from one clock, within the bound
/// reserved in the catalog.
/// </summary>
/// <remarks>
/// The source reserves the next batch in the background once half of the current one is
/// used, and a caller waits only when the clock reaches the bound before that reservation is
/// durable. After a restart the clock starts at the last reserved bound, so ids and
/// timestamps never repeat and never go backward.
/// </remarks>
internal sealed class DomainTimestampSource
{
  const long HalfBatch = ClusterCatalog.TimestampBatch / 2;

  readonly ClusterCatalog Catalog;
  readonly long DomainId;
  readonly Lock ReserveLock = new();
  long Clock;
  long LastCommit;
  long Bound;
  Task Reservation;

  internal DomainTimestampSource(ClusterCatalog catalog, long domainId, long start, long bound)
  {
    Catalog = catalog;
    DomainId = domainId;
    Clock = start;
    LastCommit = start;
    Bound = bound;
  }

  /// <summary>The timestamp of the domain's latest commit, or the clock's start.</summary>
  internal long LastCommitTimestamp => Volatile.Read(ref LastCommit);

  /// <summary>Issues the next id or timestamp.</summary>
  internal long Next()
  {
    var value = Interlocked.Increment(ref Clock);
    if (value > Volatile.Read(ref Bound) - HalfBatch)
      Reserve(value);
    return value;
  }

  /// <summary>Issues the timestamp of a commit and publishes it as the latest commit.</summary>
  internal long Commit()
  {
    var timestamp = Next();
    var current = Volatile.Read(ref LastCommit);
    while (current < timestamp)
    {
      var observed = Interlocked.CompareExchange(ref LastCommit, timestamp, current);
      if (observed == current)
        break;
      current = observed;
    }
    return timestamp;
  }

  void Reserve(long value)
  {
    while (true)
    {
      Task reservation;
      lock (ReserveLock)
      {
        if (value <= Bound - HalfBatch)
          return;
        reservation = Reservation ??= Task.Run(ReserveNextBatch);
        // The value is within the bound: the reservation can finish in the background.
        if (value <= Bound)
          return;
      }
      // A failed reservation fails the caller that needs it; the next caller retries.
      reservation.GetAwaiter().GetResult();
    }
  }

  void ReserveNextBatch()
  {
    try
    {
      var next = Volatile.Read(ref Bound) + ClusterCatalog.TimestampBatch;
      Catalog.ReserveTimestamps([(DomainId, next)]);
      Volatile.Write(ref Bound, next);
    }
    finally
    {
      lock (ReserveLock)
        Reservation = null;
    }
  }
}
