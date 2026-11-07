using ZoneTree.Logger;

namespace ZoneTree.Cluster;

/// <summary>
/// Reserves the next timestamp batches of a cluster's domains on a dedicated thread, so no
/// caller performs disk I/O to issue an id or timestamp, and a busy thread pool cannot delay
/// a reservation. The domains that need a batch at the same time share one durable catalog
/// commit.
/// </summary>
internal sealed class TimestampReservationWorker : IDisposable
{
  readonly ClusterCatalog Catalog;
  readonly ILogger Logger;
  readonly object Gate = new();
  readonly HashSet<DomainTimestampSource> Pending = [];
  Thread Worker;
  bool Stopping;

  internal TimestampReservationWorker(ClusterCatalog catalog, ILogger logger)
  {
    Catalog = catalog;
    Logger = logger;
  }

  /// <summary>
  /// Asks for the next batch of a domain. The source learns the outcome through
  /// <see cref="DomainTimestampSource.OnReserved"/> or <see cref="DomainTimestampSource.OnReservationFailed"/>.
  /// </summary>
  internal void Request(DomainTimestampSource source)
  {
    lock (Gate)
    {
      if (!Stopping)
      {
        Pending.Add(source);
        if (Worker == null)
        {
          Worker = new Thread(Run)
          {
            IsBackground = true,
            Name = "ZoneTree cluster timestamp reservation"
          };
          Worker.Start();
        }
        Monitor.Pulse(Gate);
        return;
      }
    }
    source.OnReservationFailed(new global::System.ObjectDisposedException(nameof(ZoneTreeCluster)));
  }

  void Run()
  {
    while (true)
    {
      DomainTimestampSource[] sources;
      lock (Gate)
      {
        while (Pending.Count == 0 && !Stopping)
          Monitor.Wait(Gate);
        sources = Pending.ToArray();
        Pending.Clear();
        if (Stopping)
        {
          FailStopped(sources);
          return;
        }
      }
      var bounds = sources.Select(source => source.NextBound).ToArray();
      try
      {
        Catalog.ReserveTimestamps(sources.Select((source, index) => (source.DomainId, bounds[index])).ToArray());
      }
      catch (Exception error)
      {
        Logger.LogError(error);
        foreach (var source in sources)
          source.OnReservationFailed(error);
        continue;
      }
      for (var index = 0; index < sources.Length; ++index)
        sources[index].OnReserved(bounds[index]);
    }
  }

  static void FailStopped(DomainTimestampSource[] sources)
  {
    foreach (var source in sources)
      source.OnReservationFailed(new global::System.ObjectDisposedException(nameof(ZoneTreeCluster)));
  }

  /// <summary>
  /// Stops the worker after its current reservation. Requests still pending, and later
  /// requests, fail, so no caller waits for a batch that will never come.
  /// </summary>
  public void Dispose()
  {
    Thread worker;
    lock (Gate)
    {
      if (Stopping)
        return;
      Stopping = true;
      worker = Worker;
      Monitor.Pulse(Gate);
    }
    worker?.Join();
  }
}
