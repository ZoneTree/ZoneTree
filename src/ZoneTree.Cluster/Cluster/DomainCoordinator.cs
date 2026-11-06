using System.Runtime.ExceptionServices;
using ZoneTree.Exceptions;
using ZoneTree.Logger;

namespace ZoneTree.Cluster;

/// <summary>The storage side of a coordinated column, as its domain's coordinator uses it.</summary>
internal interface ICoordinatedStorage
{
  /// <summary>
  /// Starts a sync of the column's write ahead log if the column wrote commit records since
  /// its last sync, or returns null. A closed or dropped column has nothing to sync.
  /// </summary>
  Task StartSync();

  /// <summary>
  /// Handles a failed sync. Returns true if the failure does not count: the column was
  /// dropped meanwhile.
  /// </summary>
  bool OnSyncFailed(Exception error);

  /// <summary>Called after the domain's durable timestamp advanced.</summary>
  void OnDomainDurable();

  /// <summary>The outcome of a transaction in the column, if the column knows it.</summary>
  TransactionOutcome GetOutcome(long transactionId);
}

/// <summary>
/// Coordinates the transactions of a domain across its columns: the clock, the snapshots, the
/// order of the commits and their durability.
/// </summary>
/// <remarks>
/// <para>
/// A domain sync syncs every column that wrote commit records since its last sync, in parallel,
/// and nothing else: every commit at or below the written timestamp it started with is then
/// durable, which only this process needs to know. A commit is complete when every column it
/// wrote holds its commit record, and the commits are numbered without gaps, so recovery
/// finds what survived in the columns themselves: the commits before the first number it
/// cannot find complete.
/// </para>
/// <para>
/// Syncs run on a dedicated thread, which syncs the columns in parallel and publishes the
/// durable commits it made durable. Callers that
/// block on a durable commit then never wait for a thread pool thread.
/// A failed sync stops the domain's commits until the cluster reopens, and recovery decides
/// the outcome of the commits it left in doubt.
/// </para>
/// </remarks>
internal sealed class DomainCoordinator
{
  sealed class DurableWaiter(long timestamp, Action publish)
  {
    internal readonly long Timestamp = timestamp;
    internal readonly Action Publish = publish;
    internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
  }

  readonly ZoneTreeCluster Cluster;
  readonly object SyncGate = new();
  readonly List<DurableWaiter> Waiters = [];
  readonly Lock AutoSyncLock = new();
  long durableTimestamp;
  long durableSequence;
  Thread SyncThread;
  bool Stopping;
  Exception CommitFailure;
  TimeSpan autoSyncInterval;
  CancellationTokenSource AutoSyncCancellation;
  Task AutoSyncTask;

  internal long DomainId { get; }

  /// <summary>The domain, set once the domain is created.</summary>
  internal ClusterDomain Domain { get; set; }

  internal ITimestampOracle Clock { get; }

  internal CommitSequencer Sequencer { get; }

  internal DomainSnapshots Snapshots { get; }

  internal DomainCoordinator(ZoneTreeCluster cluster, long domainId, ITimestampOracle clock,
      long clockStart)
  {
    Cluster = cluster;
    DomainId = domainId;
    Clock = clock;
    Sequencer = new CommitSequencer(clockStart);
    Snapshots = new DomainSnapshots(Sequencer, clock);
    // Recovery left every earlier commit durable or rolled back.
    durableTimestamp = durableSequence = clockStart;
  }

  /// <summary>Every commit at or below it is complete and synced in every column.</summary>
  internal long DurableTimestamp => Volatile.Read(ref durableTimestamp);

  /// <summary>
  /// Every commit at or below this sequence number is complete and synced in every column.
  /// </summary>
  internal long DurableSequence => Volatile.Read(ref durableSequence);

  internal void ThrowIfCommitsStopped()
  {
    var failure = Volatile.Read(ref CommitFailure);
    if (failure != null)
      throw new TransactionalTreeCommitsStoppedException(failure);
  }

  /// <summary>Takes the timestamp of a commit and registers it in commit order.</summary>
  internal CommitSequencer.Slot BeginCommit()
  {
    ThrowIfCommitsStopped();
    return Sequencer.Begin(Clock);
  }

  /// <summary>Makes every commit that wrote its records before the call durable.</summary>
  internal Task SyncAsync() => WaitDurable(Sequencer.Written.Timestamp, null);

  /// <summary>
  /// Makes every commit that took its timestamp before the call durable, after waiting for
  /// the ones still writing their records. Returns the durable sequence number, which covers
  /// them.
  /// Blocks; for merges and drops, which are rare and run off the commit path.
  /// </summary>
  internal long MakeIssuedCommitsDurable()
  {
    var issued = Clock.Current;
    var wait = new SpinWait();
    while (!Sequencer.IsWrittenThrough(issued))
    {
      ThrowIfCommitsStopped();
      wait.SpinOnce();
    }
    SyncAsync().GetAwaiter().GetResult();
    return DurableSequence;
  }

  /// <summary>
  /// Waits until every commit at or below the timestamp is durable. The sync thread runs the
  /// publish action of a durable commit before it completes the wait.
  /// </summary>
  internal Task WaitDurable(long timestamp, Action publish)
  {
    lock (SyncGate)
    {
      if (CommitFailure != null)
        return Task.FromException(new TransactionalTreeCommitsStoppedException(CommitFailure));
      if (Stopping)
        return Task.FromException(new global::System.ObjectDisposedException(nameof(DomainCoordinator)));
      if (timestamp <= durableTimestamp && publish == null)
        return Task.CompletedTask;
      var waiter = new DurableWaiter(timestamp, publish);
      Waiters.Add(waiter);
      if (SyncThread == null)
      {
        SyncThread = new Thread(RunSync)
        {
          IsBackground = true,
          Name = "ZoneTree cluster domain sync"
        };
        SyncThread.Start();
      }
      Monitor.Pulse(SyncGate);
      return waiter.Completion.Task;
    }
  }

  void RunSync()
  {
    var backoff = new SpinWait();
    while (true)
    {
      lock (SyncGate)
      {
        while (Waiters.Count == 0 && !Stopping)
          Monitor.Wait(SyncGate);
        if (Waiters.Count == 0)
          return;
      }
      // The written timestamp is read before the syncs, so they cover every commit at or
      // below it. Commits that write later wait for the next pass.
      var target = Sequencer.Written;
      var advanced = false;
      try
      {
        SyncColumns();
        // A commit that stopped the commits did so before its timestamp was written, so
        // a target that covers it sees the failure here, and the durable timestamp stays.
        ThrowIfCommitsStopped();
        if (target.Timestamp > durableTimestamp)
        {
          // The sequence first, so a reader never sees it ahead of the timestamp.
          Volatile.Write(ref durableSequence, target.Sequence);
          Volatile.Write(ref durableTimestamp, target.Timestamp);
          NotifyDurable();
          advanced = true;
        }
      }
      catch (Exception error)
      {
        Cluster.Logger.LogError(error);
        FailWaiters(error);
        continue;
      }
      CompleteDurableWaiters();
      // A waiter behind a commit that is still writing its records finds nothing new to
      // make durable. The pass yields to that commit instead of spinning on it.
      if (advanced)
        backoff.Reset();
      else
        backoff.SpinOnce(sleep1Threshold: -1);
    }
  }

  /// <summary>
  /// Syncs every column that wrote commit records. The syncs run in parallel: each column
  /// has its own write ahead log.
  /// </summary>
  void SyncColumns()
  {
    List<(ICoordinatedStorage Storage, Task Sync)> syncs = null;
    Exception failure = null;
    foreach (var column in Cluster.OpenColumnsOf(Domain))
    {
      var storage = (ICoordinatedStorage)column;
      try
      {
        var sync = storage.StartSync();
        if (sync != null)
          (syncs ??= []).Add((storage, sync));
      }
      catch (Exception error) when (!storage.OnSyncFailed(error))
      {
        failure ??= error;
      }
    }
    if (syncs != null)
    {
      foreach (var (storage, sync) in syncs)
      {
        try { sync.GetAwaiter().GetResult(); }
        catch (Exception error) when (!storage.OnSyncFailed(error))
        {
          failure ??= error;
        }
      }
    }
    if (failure != null)
      ExceptionDispatchInfo.Throw(failure);
  }

  void NotifyDurable()
  {
    foreach (var column in Cluster.OpenColumnsOf(Domain))
      ((ICoordinatedStorage)column).OnDomainDurable();
  }

  void CompleteDurableWaiters()
  {
    DurableWaiter[] completed;
    lock (SyncGate)
    {
      var durable = durableTimestamp;
      completed = Waiters.Where(waiter => waiter.Timestamp <= durable).OrderBy(waiter => waiter.Timestamp).ToArray();
      foreach (var waiter in completed)
        Waiters.Remove(waiter);
    }
    // Durable commits become visible in commit order, on this thread.
    foreach (var waiter in completed)
    {
      try
      {
        waiter.Publish?.Invoke();
        waiter.Completion.TrySetResult();
      }
      catch (Exception error)
      {
        waiter.Completion.TrySetException(error);
      }
    }
  }

  /// <summary>
  /// Stops the domain's commits until the cluster reopens. A commit that failed after it took
  /// its sequence number passes its slot: the slot never completes, so no later commit becomes
  /// visible or durable, and recovery cuts at that number.
  /// </summary>
  internal void StopCommits(Exception error, CommitSequencer.Slot slot = null)
  {
    FailWaiters(error);
    if (slot != null)
      Sequencer.Stop(slot, error);
  }

  /// <summary>
  /// Stops the domain's commits after a failed sync. The waiting durable commits stay in
  /// doubt: their records may be on the disk, and recovery decides their outcome.
  /// </summary>
  void FailWaiters(Exception error)
  {
    DurableWaiter[] failed;
    lock (SyncGate)
    {
      CommitFailure ??= error;
      failed = Waiters.ToArray();
      Waiters.Clear();
    }
    foreach (var waiter in failed)
      waiter.Completion.TrySetException(new TransactionalTreeCommitsStoppedException(error));
  }

  /// <summary>The interval of the automatic domain syncs; zero disables them.</summary>
  internal TimeSpan AutoSyncInterval
  {
    get => autoSyncInterval;
    set
    {
      ZoneTreeCluster.ValidateSyncInterval(value);
      lock (AutoSyncLock)
      {
        if (value == autoSyncInterval)
          return;
        StopAutoSync();
        autoSyncInterval = value;
        if (value != TimeSpan.Zero)
          StartAutoSync(value);
      }
    }
  }

  void StartAutoSync(TimeSpan interval)
  {
    var cancellation = new CancellationTokenSource();
    AutoSyncCancellation = cancellation;
    AutoSyncTask = Task.Run(async () =>
    {
      using var timer = new PeriodicTimer(interval);
      try
      {
        while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
        {
          try { await SyncAsync().ConfigureAwait(false); }
          catch (Exception error) { Cluster.Logger.LogError(error); }
        }
      }
      catch (OperationCanceledException) { }
    });
  }

  void StopAutoSync()
  {
    var cancellation = AutoSyncCancellation;
    if (cancellation == null)
      return;
    cancellation.Cancel();
    try { AutoSyncTask.GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { }
    cancellation.Dispose();
    AutoSyncCancellation = null;
    AutoSyncTask = null;
  }

  /// <summary>
  /// Stops the syncs after the pending ones. A clean shutdown then syncs every column and
  /// records that the domain closed cleanly, so recovery rolls back nothing.
  /// </summary>
  internal void Shutdown(bool clean)
  {
    Sequencer.Close();
    lock (AutoSyncLock)
      StopAutoSync();
    Thread thread;
    lock (SyncGate)
    {
      Stopping = true;
      thread = SyncThread;
      Monitor.Pulse(SyncGate);
    }
    thread?.Join();
    if (!clean || Volatile.Read(ref CommitFailure) != null)
      return;
    try
    {
      // A commit still in flight may have written some of its records, so the shutdown is
      // clean only without one; recovery then rolls back the commits above the timestamp.
      // The progress is read before the syncs, so they cover it.
      var (written, isIdle) = Sequencer.GetProgress();
      SyncColumns();
      Cluster.Catalog.SetDomainDurable(DomainId, written.Sequence, isClean: isIdle);
    }
    catch (Exception error)
    {
      Cluster.Logger.LogError(error);
    }
  }
}
