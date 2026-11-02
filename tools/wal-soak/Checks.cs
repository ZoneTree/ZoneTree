using ZoneTree.AbstractFileStream;
using ZoneTree.Transactional;
using ZoneTree.UnitTests;

namespace ZoneTree.WalSoak;

static class Checks
{
  public static async Task Run(CaseConfig config)
  {
    using var ownership = new FileStream(Path.Combine(config.Directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var wire = new Wire();
    LaneModel.SelfCheck(config);
    await VisibilityAndFailure(config, wire);
    wire.Send(new("check", Text: "Native durable visibility, cancellation, conflict reservation, rotation, and sync failure probes passed."));
    await TornPowerLoss(config, wire);
  }

  static async Task VisibilityAndFailure(CaseConfig config, Wire wire)
  {
    var path = Path.Combine(config.Directory, "probes");
    var files = new NativeFiles();
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var expectedCloseFailure = false;
    var tree = Database.Factory(config, files, wire, path).CreateTransactional();
    try
    {
      using (var baseline = tree.BeginTransaction()) { baseline.Upsert(1, new byte[] { 10 }); await baseline.CommitDurableAsync(); }
      using var oldSnapshot = tree.BeginReadOnlyTransaction();
      var timestamp = tree.Maintenance.LastCommitTimestamp;
      var armed = 1;
      files.BeforeSync = _ =>
      {
        if (Interlocked.Exchange(ref armed, 0) != 1) return;
        entered.Set();
        if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Visibility probe did not release fsync.");
      };
      using var candidate = tree.BeginTransaction();
      candidate.Upsert(1, new byte[] { 20 });
      using var cancel = new CancellationTokenSource();
      var pending = candidate.CommitDurableAsync(cancel.Token);
      using var follower = tree.BeginTransaction();
      follower.Upsert(3, new byte[] { 40 });
      Task? following = null;
      try
      {
        LaneModel.Require(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(15))), "Durable commit never reached native fsync.");
        using (var snapshot = tree.BeginReadOnlyTransaction())
          LaneModel.Require(snapshot.TryGet(1, out var value) && value[0] == 10, "An unsynced durable commit became visible.");
        LaneModel.Require(tree.Maintenance.LastCommitTimestamp == timestamp, "Durable commit published its timestamp before fsync.");
        using (var conflict = tree.BeginTransaction())
        { conflict.Upsert(1, new byte[] { 99 }); LaneModel.Require(!conflict.TryCommit(), "Prepared durable write was not reserved against conflicting commits."); }
        await Task.Run(() => tree.Maintenance.ZoneTree.Maintenance.MoveMutableSegmentForward()).WaitAsync(TimeSpan.FromSeconds(10));
        following = Task.Run(follower.Commit);
        await Task.Delay(30);
        using (var snapshot = tree.BeginReadOnlyTransaction())
          LaneModel.Require(!snapshot.ContainsKey(3) && !following.IsCompleted && tree.Maintenance.LastCommitTimestamp == timestamp,
            "A later ordinary commit published ahead of a blocked durable commit.");
        cancel.Cancel();
        try { await pending; throw new InvalidDataException("Cancelled waiter unexpectedly completed while fsync was blocked."); }
        catch (OperationCanceledException) { }
        LaneModel.Require(candidate.State == TransactionState.Committing, "Cancellation rolled back a prepared durable commit.");
      }
      finally { release.Set(); }
      var deadline = DateTime.UtcNow.AddSeconds(15);
      while (candidate.State == TransactionState.Committing && DateTime.UtcNow < deadline) await Task.Delay(1);
      LaneModel.Require(candidate.State == TransactionState.Committed, "Cancelled waiter did not eventually publish after fsync.");
      await (following ?? throw new InvalidDataException("Follower was never started.")).WaitAsync(TimeSpan.FromSeconds(15));
      using (var snapshot = tree.BeginReadOnlyTransaction())
        LaneModel.Require(snapshot.TryGet(1, out var value) && value[0] == 20, "Durable commit not visible after fsync.");
      LaneModel.Require(oldSnapshot.TryGet(1, out var original) && original[0] == 10, "Old snapshot changed across durable publication.");
      files.BeforeSync = null;
      var fail = 1;
      files.BeforeSync = _ => { if (Interlocked.Exchange(ref fail, 0) == 1) throw new IOException("soak injected fsync failure"); };
      using var uncertain = tree.BeginTransaction();
      uncertain.Upsert(2, new byte[] { 30 });
      var failed = false;
      try { await uncertain.CommitDurableAsync(); }
      catch (Exception error) when (error.ToString().Contains("soak injected fsync failure", StringComparison.Ordinal)) { failed = true; }
      LaneModel.Require(failed && uncertain.State == TransactionState.InDoubt, "Failed fsync did not leave the commit InDoubt.");
      using (var snapshot = tree.BeginReadOnlyTransaction()) LaneModel.Require(!snapshot.ContainsKey(2), "InDoubt commit became visible.");
      files.BeforeSync = null;
      expectedCloseFailure = true;
    }
    finally
    {
      try { tree.Dispose(); }
      catch (Exception error) when (expectedCloseFailure && error.ToString().Contains("soak injected fsync failure", StringComparison.Ordinal)) { }
    }
    using var reopened = Database.Factory(config, new NativeFiles(), wire, path).OpenTransactional();
    using var read = reopened.BeginReadOnlyTransaction();
    LaneModel.Require(read.TryGet(1, out var recovered) && recovered[0] == 20, "Successful durable commit was lost after a later failed fsync.");
  }

  sealed record Image(PowerLossFileStreamProvider Files, long Floor, long Ceiling, int Number);

  static async Task TornPowerLoss(CaseConfig original, Wire wire)
  {
    var config = original with { Lanes = 1, Options = original.Options with { Accounts = 16, Slots = 16 }, Profile = original.Profile with { MutableSize = 128, ChunkSize = 4096 } };
    var provider = new PowerLossFileStreamProvider();
    var images = new List<Image>();
    var random = new Random(config.Seed);
    var samples = 0;
    long floor = 0, ceiling = 0;
    var capturing = false;
    var imageLock = new object();
    void Capture(string _)
    {
      if (!capturing) return;
      lock (imageLock)
      {
        var number = ++samples;
        var slot = number <= 8 ? number - 1 : random.Next(number);
        if (slot >= 8) return;
        var minimum = Interlocked.Read(ref floor); // Lower bound precedes the sampled image.
        var sampled = provider.SimulateTornPowerLoss(random);
        var image = new Image(sampled, minimum, Interlocked.Read(ref ceiling), number);
        if (slot == images.Count) images.Add(image); else images[slot] = image;
      }
    }
    using (var tree = Database.Factory(config, provider, wire, "simulated").CreateTransactional())
    {
      await Database.Initialize(tree, config);
      var model = LaneModel.Create(config)[0];
      provider.BeforeDiskSync = Capture;
      capturing = true;
      for (var seq = 1; seq <= 64; ++seq)
      {
        Interlocked.Exchange(ref ceiling, seq);
        using var tx = tree.BeginTransaction();
        LaneModel.Write(tx, model.Advance());
        if (seq % 3 == 0)
        {
          await tx.CommitDurableAsync();
          Interlocked.Exchange(ref floor, seq);
        }
        else tx.Commit();
        if (seq % 8 == 0)
        {
          tree.Maintenance.ZoneTree.Maintenance.MoveMutableSegmentForward();
          tree.Maintenance.ZoneTree.Maintenance.StartMergeOperation()?.Join();
          tree.Maintenance.SaveMetaData();
        }
      }
      Capture("");
      capturing = false;
      provider.BeforeDiskSync = null!;
    }
    LaneModel.Require(images.Count == 8, "Power-loss sampler did not capture enough images.");
    foreach (var image in images)
    {
      // Opening may repair a tail. Keep an untouched image for failure evidence.
      var evidence = image.Files.SimulatePowerLoss();
      try
      {
        using (var tree = Database.Factory(config, image.Files, wire, "simulated").OpenTransactional())
        {
          var models = LaneModel.Create(config);
          long seq;
          using (var snapshot = tree.BeginReadOnlyTransaction()) seq = LaneModel.Check(snapshot, config, models, new[] { image.Floor }, new[] { image.Ceiling })[0];
          using var next = tree.BeginTransaction();
          LaneModel.Write(next, models[0].Advance());
          await next.CommitDurableAsync();
          using var check = tree.BeginReadOnlyTransaction();
          LaneModel.Check(check, config, LaneModel.Create(config), new[] { seq + 1 }, new[] { seq + 1 });
        }
        using var reopened = Database.Factory(config, image.Files, wire, "simulated").OpenTransactional();
        using var read = reopened.BeginReadOnlyTransaction();
        LaneModel.Check(read, config, LaneModel.Create(config), new[] { image.Floor + 1 });
      }
      catch
      {
        var output = Path.Combine(original.Directory, "simulation-failure", image.Number.ToString());
        Dump(evidence, "simulated", Path.Combine(output, "database"));
        Directory.CreateDirectory(output);
        Json.Save(Path.Combine(output, "image.json"), new { image.Floor, image.Ceiling, image.Number, config });
        throw;
      }
    }
    wire.Send(new("check", Text: $"Verified {images.Count} torn power-loss images from {samples} sampled sync boundaries, including continued writes and reopen."));
  }

  static void Dump(IFileStreamProvider files, string source, string destination)
  {
    Directory.CreateDirectory(destination);
    foreach (var file in files.GetFiles(source)) File.WriteAllBytes(Path.Combine(destination, Path.GetFileName(file)), files.ReadAllBytes(file));
    foreach (var folder in files.GetDirectories(source)) Dump(files, folder, Path.Combine(destination, Path.GetFileName(folder)));
  }

  public static async Task Verify(CaseConfig config, string target, bool resumed)
  {
    using var ownership = new FileStream(Path.Combine(config.Directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var wire = new Wire();
    var receipts = Receipts.Load(Path.Combine(config.Directory, "oracle.jsonl"), config.Lanes);
    var floor = receipts.Durable.ToArray();
    if (config.Profile.Wal == ZoneTree.Options.WriteAheadLogMode.Shared)
      for (var lane = 0; lane < floor.Length; ++lane) floor[lane] = Math.Max(floor[lane], receipts.Committed[lane]);
    long[] recovered;
    using (var tree = Database.Factory(config, new NativeFiles(), wire, target).OpenTransactional())
    {
      var models = LaneModel.Create(config);
      using (var read = tree.BeginReadOnlyTransaction()) recovered = LaneModel.Check(read, config, models, floor, resumed ? null : receipts.Issued);
      Receipts.CheckGlobalPrefix(Path.Combine(config.Directory, "oracle.jsonl"), recovered);
      using var tx = tree.BeginTransaction();
      foreach (var model in models) LaneModel.Write(tx, model.Advance());
      await tx.CommitDurableAsync();
    }
    using (var tree = Database.Factory(config, new NativeFiles(), wire, target).OpenTransactional())
    using (var snapshot = tree.BeginReadOnlyTransaction())
      LaneModel.Check(snapshot, config, LaneModel.Create(config), recovered.Select(x => x + 1).ToArray(), recovered.Select(x => x + 1).ToArray());
    wire.Send(new("verified", Sequences: recovered, Data: new { required = floor, issued = receipts.Issued, resumed }));
  }
}
