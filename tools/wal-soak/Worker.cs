using System.Diagnostics;
using ZoneTree.Transactional;

namespace ZoneTree.WalSoak;

static class Worker
{
  public static async Task Run(CaseConfig config)
  {
    using var ownership = new FileStream(Path.Combine(config.Directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var wire = new Wire();
    var files = new NativeFiles();
    using var stop = new CancellationTokenSource();
    var armed = 0;
    var reached = 0;
    void Boundary(string phase)
    {
      if (Volatile.Read(ref armed) == 0 || config.Ending != phase || Interlocked.CompareExchange(ref reached, 1, 0) != 0) return;
      wire.Send(new("crash-point", Phase: phase));
      using var never = new ManualResetEventSlim();
      never.Wait(); // The supervisor kills this process at this exact boundary.
    }
    files.BeforeSync = _ => Boundary("before-fsync");
    files.AfterSync = _ => Boundary("after-fsync");
    files.AfterRename = path => { if (Path.GetFileName(path) == "0.json") Boundary("after-meta-rename"); };
    using var tree = Database.Factory(config, files, wire).CreateTransactional();
    using var maintainer = tree.CreateMaintainer();
    maintainer.MaximumReadOnlySegmentCount = 8;
    maintainer.ThresholdForMergeOperationStart = config.Profile.MutableSize;
    maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromSeconds(3);
    maintainer.BlockCacheLifeTime = TimeSpan.FromSeconds(3);
    var maintenance = tree.Maintenance.ZoneTree.Maintenance;
    long rotations = 0, merges = 0, commits = 0, rollbacks = 0, audits = 0, cancellations = 0;
    maintenance.OnMutableSegmentMovedForward += _ => Interlocked.Increment(ref rotations);
    maintenance.OnDiskSegmentCreated += (_, _, _) => Boundary("merge-created");
    maintenance.OnMergeOperationEnded += (_, result) =>
    {
      if (result == MergeResult.FAILURE) { wire.Send(new("engine-error", Text: "Background merge failed.")); stop.Cancel(); }
      else if (result == MergeResult.SUCCESS) Interlocked.Increment(ref merges);
    };
    maintenance.OnBottomSegmentsMergeOperationEnded += (_, result) =>
    {
      if (result == MergeResult.FAILURE) { wire.Send(new("engine-error", Text: "Bottom merge failed.")); stop.Cancel(); }
      else if (result == MergeResult.SUCCESS) Interlocked.Increment(ref merges);
    };
    await Database.Initialize(tree, config);
    tree.AutoSyncInterval = config.Index % 2 == 0 && config.Profile.Workload != "queued" ? TimeSpan.FromMilliseconds(250) : TimeSpan.Zero;
    var sequences = new long[config.Lanes];
    var syncedSequences = new long[config.Lanes];
    void MarkSynced(int lane, long seq)
    {
      var previous = Interlocked.Read(ref syncedSequences[lane]);
      while (previous < seq)
      {
        var observed = Interlocked.CompareExchange(ref syncedSequences[lane], seq, previous);
        if (observed == previous) return;
        previous = observed;
      }
    }
    long[] Cut() => sequences.Select((_, i) => Interlocked.Read(ref sequences[i])).ToArray();
    _ = Task.Run(async () =>
    {
      while (await Console.In.ReadLineAsync() is { } command)
        if (command == "stop") { stop.Cancel(); return; }
        else if (command == "arm") Interlocked.Exchange(ref armed, 1);
      stop.Cancel(); // A dead supervisor must not leave an orphan writing indefinitely.
    });
    async Task Guard(Func<Task> action)
    {
      try { await action(); }
      catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
      catch (Exception error) { wire.Send(new("fatal", Text: error.ToString())); stop.Cancel(); throw; }
    }
    var jobs = new List<Task>();
    for (var laneIndex = 0; laneIndex < config.Lanes; ++laneIndex)
    {
      var lane = laneIndex;
      jobs.Add(Task.Run(() => Guard(async () =>
      {
        var model = LaneModel.Create(config)[lane];
        var pace = Stopwatch.StartNew();
        while (!stop.IsCancellationRequested && model.Sequence < config.Options.MaxSequence)
        {
          // Keep intentional async overload bounded. This is admission control in
          // the test client, not a change to ZoneTree's append or rotation paths.
          while (model.Sequence - Interlocked.Read(ref syncedSequences[lane]) >= config.Options.MaxOutstanding)
            await Task.Delay(10, stop.Token);
          var changes = model.Advance();
          wire.Send(new("issued", lane, model.Sequence));
          // An aborted attempt writes the same candidate but must never become visible.
          if (model.Sequence % 11 == 0)
          {
            using var aborted = tree.BeginTransaction();
            LaneModel.Write(aborted, changes);
            aborted.Upsert(LaneModel.Key(lane, 9), new byte[] { 1, 2, 3 });
            aborted.Rollback();
            Interlocked.Increment(ref rollbacks);
          }
          using var tx = tree.BeginTransaction(model.Sequence % 2 == 0 ? TransactionIsolationLevel.Snapshot : TransactionIsolationLevel.Serializable);
          LaneModel.Write(tx, changes);
          var durable = config.Profile.Workload == "durable" || config.Profile.Workload != "queued" && model.Sequence % 3 == 0;
          if (durable)
          {
            using var cancel = new CancellationTokenSource();
            if (model.Sequence % 101 == 0) cancel.CancelAfter(1);
            try { ModelSuccess(await tx.TryCommitDurableAsync(cancel.Token)); }
            catch (OperationCanceledException)
            {
              Interlocked.Increment(ref cancellations);
              // Cancellation stops waiting, not a prepared commit. Do not reuse its lane yet.
              var timer = Stopwatch.StartNew();
              while (tx.State == TransactionState.Committing && timer.Elapsed.TotalSeconds < config.Options.StallSeconds)
                await Task.Delay(1);
              if (tx.State == TransactionState.Active) ModelSuccess(await tx.TryCommitDurableAsync());
              else LaneModel.Require(tx.State == TransactionState.Committed, $"Cancelled durable commit ended in {tx.State}.");
            }
          }
          else ModelSuccess(tx.TryCommit());
          Interlocked.Exchange(ref sequences[lane], model.Sequence);
          Interlocked.Increment(ref commits);
          wire.Send(new("commit", lane, model.Sequence, durable, Timestamp: tx.CommitTimestamp));
          if (durable) MarkSynced(lane, model.Sequence);
          if (durable) Boundary("after-durable-ack");
          if (config.Seconds > config.Options.EpochSeconds)
          {
            var delay = model.Sequence * 1000d / config.Options.LongRate - pace.Elapsed.TotalMilliseconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), stop.Token);
          }
        }
        if (model.Sequence >= config.Options.MaxSequence) { wire.Send(new("budget", Text: "Sequence budget reached; ending this epoch cleanly.")); stop.Cancel(); }
      })));
    }
    for (var readerIndex = 0; readerIndex < config.Options.Readers; ++readerIndex)
    {
      var reader = readerIndex;
      jobs.Add(Task.Run(() => Guard(async () =>
      {
        var models = LaneModel.Create(config);
        var iteration = 0;
        while (!stop.IsCancellationRequested)
        {
          using var snapshot = tree.BeginReadOnlyTransaction();
          LaneModel.Check(snapshot, config, models);
          Interlocked.Increment(ref audits);
          await Task.Delay(++iteration % 20 == 0 && reader == 0 ? config.Options.SnapshotSeconds * 1000 : 100, stop.Token);
          LaneModel.Check(snapshot, config, models);
          Interlocked.Increment(ref audits);
        }
      })));
    }
    jobs.Add(Task.Run(() => Guard(async () =>
    {
      var iteration = 0;
      while (!stop.IsCancellationRequested)
      {
        await Task.Delay(500, stop.Token);
        var cut = Cut(); // Capture BEFORE the sync; later commits are not covered by this receipt.
        if (config.Profile.Workload != "queued" || iteration % 20 == 0)
        {
          await tree.SyncWALAsync();
          wire.Send(new("synced", Sequences: cut));
          for (var lane = 0; lane < config.Lanes; ++lane) MarkSynced(lane, cut[lane]);
        }
        if (++iteration % 4 == 0) { maintenance.MoveMutableSegmentForward(); maintainer.StartMerge(); }
        if (iteration % 8 == 0) maintainer.StartBottomSegmentsMerge();
        if (iteration % 6 == 0) tree.Maintenance.SaveMetaData();
      }
    })));
    jobs.Add(Task.Run(() => Guard(async () =>
    {
      while (!stop.IsCancellationRequested)
      {
        wire.Send(new("metrics", Sequences: Cut(), Data: new { commits = Interlocked.Read(ref commits), rollbacks = Interlocked.Read(ref rollbacks), audits = Interlocked.Read(ref audits), cancellations = Interlocked.Read(ref cancellations), rotations = Interlocked.Read(ref rotations), merges = Interlocked.Read(ref merges), readOnlySegments = maintenance.ReadOnlySegmentsCount, bottomSegments = maintenance.BottomSegments.Count, activeTransactions = tree.Maintenance.ActiveTransactionCount }));
        await Task.Delay(1000, stop.Token);
      }
    })));
    wire.Send(new("ready"));
    await Task.WhenAll(jobs);
    tree.AutoSyncInterval = TimeSpan.Zero;
    await tree.SyncWALAsync();
    using (var snapshot = tree.BeginReadOnlyTransaction()) LaneModel.Check(snapshot, config, LaneModel.Create(config), Cut(), Cut());
    await maintainer.WaitForBackgroundThreadsAsync();
    wire.Send(new("final", Sequences: Cut()));
  }

  static void ModelSuccess(bool success) => LaneModel.Require(success, "Independent writer unexpectedly conflicted.");
}
