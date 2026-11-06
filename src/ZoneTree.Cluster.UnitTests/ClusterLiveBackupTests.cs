using System.Collections.Concurrent;
using ZoneTree.Backup;
using ZoneTree.Cluster;

namespace ZoneTree.Cluster.UnitTests;

/// <summary>
/// Live backups of a whole cluster while it commits, and restores that keep every domain's
/// cut whole in every column.
/// </summary>
public sealed class ClusterLiveBackupTests
{
  const string Domain = "accounts";
  string Root;
  readonly ZoneTreeClusterFactory Factory = new();

  [SetUp]
  public void SetUp()
  {
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "ZoneTree.sln")))
      root = root.Parent;
    Assert.That(root, Is.Not.Null);
    Root = Path.Combine(root.FullName, ".tmp", "cluster-backup", "tests", Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(Root))
      Directory.Delete(Root, recursive: true);
  }

  string Data(string name) => Path.Combine(Root, name);

  LocalLiveBackupOptions BackupStorage(int? keep = null)
      => new() { Directory = Data("backup"), KeepLastGenerations = keep };

  ZoneTreeClusterOptions Options(string directory, int mutableSegment = 0, bool withOthers = true)
  {
    void Configure<TKey, TValue>(ZoneTreeFactory<TKey, TValue> storage)
    {
      if (mutableSegment > 0)
        storage.SetMutableSegmentMaxItemCount(mutableSegment);
    }
    return new()
    {
      DataDirectory = directory,
      Logger = SilentTestLogger.Instance,
      Domains =
      [
        new DomainDefinition(Domain)
        {
          Columns =
          [
            new CoordinatedColumnDefinition<long, long>("left") { ConfigureStorage = Configure },
            new CoordinatedColumnDefinition<long, long>("right") { ConfigureStorage = Configure }
          ]
        }
      ],
      Columns = withOthers
          ? [new RawColumnDefinition<long, long>("cache"), new StandaloneColumnDefinition<long, long>("visits")]
          : []
    };
  }

  static void Commit(IClusterDomain domain, long key)
  {
    using var transaction = domain.BeginTransaction();
    transaction.GetColumn(domain.GetColumn<long, long>("left")).Upsert(key, key + 1);
    transaction.GetColumn(domain.GetColumn<long, long>("right")).Upsert(key, key + 1);
    transaction.Commit();
  }

  static Dictionary<long, long> Read(IClusterDomain domain, string column)
  {
    var records = new Dictionary<long, long>();
    using var transaction = domain.BeginReadOnlyTransaction();
    using var iterator = transaction.GetColumn(domain.GetColumn<long, long>(column)).CreateIterator();
    while (iterator.Next())
      records[iterator.CurrentKey] = iterator.CurrentValue;
    return records;
  }

  [Test]
  public async Task ARestoredClusterHasEveryColumnOfEveryKind()
  {
    using (var cluster = Factory.CreateInProcess(Options(Data("source"))))
    {
      var domain = cluster.GetDomain(Domain);
      for (var key = 1L; key <= 50; ++key)
        Commit(domain, key);
      cluster.GetRawColumn<long, long>("cache").Upsert(7, 70);
      cluster.GetStandaloneColumn<long, long>("visits").Execute(transaction => transaction.Upsert(3, 30));
      using var backup = cluster.CreateLiveBackup(BackupStorage());
      await backup.CreateGenerationAsync();
      Assert.That(backup.CurrentGenerationId, Is.EqualTo(1));
      Assert.That(backup.GetStatus().AttemptState, Is.EqualTo(LiveBackupAttemptState.Succeeded));
      // Commits after the generation are not in it.
      Commit(domain, 51);
    }

    using var restored = await Factory.RestoreInProcessAsync(Options(Data("restored")), BackupStorage());
    var restoredDomain = restored.GetDomain(Domain);
    var expected = Enumerable.Range(1, 50).ToDictionary(key => (long)key, key => (long)key + 1);
    Assert.That(Read(restoredDomain, "left"), Is.EqualTo(expected));
    Assert.That(Read(restoredDomain, "right"), Is.EqualTo(expected));
    Assert.That(restored.GetRawColumn<long, long>("cache").TryGet(7, out var cached) && cached == 70, Is.True);
    using var visits = restored.GetStandaloneColumn<long, long>("visits").BeginReadOnlyTransaction();
    Assert.That(visits.TryGet(3, out var count) && count == 30, Is.True);
  }

  [Test]
  public async Task GenerationsUnderConcurrentCommitsAndMergesRestoreOneCutInEveryColumn()
  {
    const int writers = 4;
    var committedBefore = new List<long[]>();
    using (var cluster = Factory.CreateInProcess(Options(Data("source"), mutableSegment: 1000, withOthers: false)))
    using (var maintainer = cluster.CreateMaintainer())
    using (var backup = cluster.CreateLiveBackup(BackupStorage()))
    {
      var domain = cluster.GetDomain(Domain);
      var committed = new ConcurrentBag<long>();
      using var stop = new CancellationTokenSource();
      var tasks = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
      {
        for (var i = 0L; !stop.IsCancellationRequested; ++i)
        {
          var key = writer * 1_000_000L + i;
          Commit(domain, key);
          committed.Add(key);
        }
      })).ToArray();
      for (var generation = 0; generation < 3; ++generation)
      {
        await Task.Delay(150);
        // Every commit that returned before the generation began is in it.
        committedBefore.Add(committed.ToArray());
        await backup.CreateGenerationAsync();
      }
      stop.Cancel();
      await Task.WhenAll(tasks);
    }

    for (var generation = 1; generation <= 3; ++generation)
    {
      using var restored = await Factory.RestoreInProcessAsync(
          Options(Data($"restored-{generation}"), withOthers: false), BackupStorage(), generation);
      var domain = restored.GetDomain(Domain);
      var left = Read(domain, "left");
      var right = Read(domain, "right");
      Assert.That(left, Is.EqualTo(right), $"generation {generation}");
      Assert.That(left.All(pair => pair.Value == pair.Key + 1), Is.True);
      var before = committedBefore[generation - 1];
      Assert.That(before.Where(key => !left.ContainsKey(key)), Is.Empty, $"generation {generation}");
      // The restored domain commits again, and a reopen keeps the commits.
      Commit(domain, -generation);
    }
    using var reopened = Factory.OpenInProcess(Options(Data("restored-3"), withOthers: false));
    Assert.That(Read(reopened.GetDomain(Domain), "right").ContainsKey(-3), Is.True);
  }

  [Test]
  public async Task AGenerationLeavesOutTransactionsThatDidNotCommit()
  {
    using (var cluster = Factory.CreateInProcess(Options(Data("source"), withOthers: false)))
    {
      var domain = cluster.GetDomain(Domain);
      Commit(domain, 1);
      // Written in both columns, not committed while the generation runs.
      using var open = domain.BeginTransaction();
      open.GetColumn(domain.GetColumn<long, long>("left")).Upsert(2, 3);
      open.GetColumn(domain.GetColumn<long, long>("right")).Upsert(2, 3);
      using var backup = cluster.CreateLiveBackup(BackupStorage());
      await backup.CreateGenerationAsync();
      open.Commit();
    }

    using var restored = await Factory.RestoreInProcessAsync(Options(Data("restored"), withOthers: false), BackupStorage());
    var restoredDomain = restored.GetDomain(Domain);
    Assert.That(Read(restoredDomain, "left").Keys, Is.EqualTo(new long[] { 1 }));
    Assert.That(Read(restoredDomain, "right").Keys, Is.EqualTo(new long[] { 1 }));
  }

  [Test]
  public async Task RetentionKeepsTheNewestGenerationsRestorable()
  {
    using (var cluster = Factory.CreateInProcess(Options(Data("source"), withOthers: false)))
    using (var backup = cluster.CreateLiveBackup(BackupStorage(keep: 1)))
    {
      var domain = cluster.GetDomain(Domain);
      for (var key = 1L; key <= 3; ++key)
      {
        Commit(domain, key);
        await backup.CreateGenerationAsync();
      }
      Assert.That(backup.CurrentGenerationId, Is.EqualTo(3));
    }
    Assert.That(Directory.GetFiles(Path.Combine(Data("backup"), "cluster"), "*.manifest"), Has.Length.EqualTo(1));
    Assert.ThrowsAsync<LiveBackupGenerationException>(async () =>
        await Factory.RestoreInProcessAsync(Options(Data("old"), withOthers: false), BackupStorage(), 1));
    using var restored = await Factory.RestoreInProcessAsync(Options(Data("restored"), withOthers: false), BackupStorage());
    Assert.That(Read(restored.GetDomain(Domain), "left").Keys, Is.EqualTo(new long[] { 1, 2, 3 }));
  }

  [Test]
  public async Task ObjectStorageBackupsRestore()
  {
    var storage = new MemoryBackupObjectStorage();
    var storageOptions = new ObjectStorageLiveBackupOptions { Prefix = "cluster-backup" };
    using (var cluster = Factory.CreateInProcess(Options(Data("source"))))
    using (var backup = cluster.CreateLiveBackup(storage, storageOptions))
    {
      var domain = cluster.GetDomain(Domain);
      for (var key = 1L; key <= 20; ++key)
        Commit(domain, key);
      cluster.GetRawColumn<long, long>("cache").Upsert(1, 10);
      await backup.CreateGenerationAsync();
    }
    using var restored = await Factory.RestoreInProcessAsync(Options(Data("restored")), storage, storageOptions);
    Assert.That(Read(restored.GetDomain(Domain), "left"), Has.Count.EqualTo(20));
    Assert.That(Read(restored.GetDomain(Domain), "right"), Has.Count.EqualTo(20));
    Assert.That(restored.GetRawColumn<long, long>("cache").ContainsKey(1), Is.True);
  }

  [Test]
  public async Task FailedGenerationsPublishNoColumnAndKeepTheLastGenerationRestorable()
  {
    var storage = new MemoryBackupObjectStorage();
    var storageOptions = new ObjectStorageLiveBackupOptions { Prefix = "cluster-backup", KeepLastGenerations = 1 };
    using (var cluster = Factory.CreateInProcess(Options(Data("source"), withOthers: false)))
    using (var backup = cluster.CreateLiveBackup(storage, storageOptions))
    {
      var domain = cluster.GetDomain(Domain);
      Commit(domain, 1);
      await backup.CreateGenerationAsync();
      var right = ((ZoneTreeCluster)cluster).Catalog.State.FindColumn("right").Id
          .ToString("D20", System.Globalization.CultureInfo.InvariantCulture);
      // The right column's uploads fail. Without the publish barrier, the left column would
      // publish two more generations and its store would evict the first one.
      storage.FailCreate = key => key.Contains("/columns/" + right + "/", StringComparison.Ordinal);
      for (var attempt = 0; attempt < 2; ++attempt)
      {
        Commit(domain, 2 + attempt);
        Assert.That(async () => await backup.CreateGenerationAsync(), Throws.Exception);
        Assert.That(backup.GetStatus().AttemptState, Is.EqualTo(LiveBackupAttemptState.Failed));
      }
      storage.FailCreate = null;
      Assert.That(backup.CurrentGenerationId, Is.EqualTo(1));
    }
    using var restored = await Factory.RestoreInProcessAsync(
        Options(Data("restored"), withOthers: false), storage, storageOptions);
    Assert.That(Read(restored.GetDomain(Domain), "left").Keys, Is.EqualTo(new long[] { 1 }));
    Assert.That(Read(restored.GetDomain(Domain), "right").Keys, Is.EqualTo(new long[] { 1 }));
  }

  [Test]
  public void AStartedBackupCreatesAGenerationAfterAMerge()
  {
    using var cluster = Factory.CreateInProcess(Options(Data("source"), withOthers: false));
    using var backup = cluster.CreateLiveBackup(BackupStorage(), new ClusterLiveBackupOptions { BackupAfterMerge = true });
    backup.Start();
    Commit(cluster.GetDomain(Domain), 1);
    var storage = cluster.GetCoordinatedColumnMaintenance<long, long>("left").ZoneTree.Maintenance;
    storage.MoveMutableSegmentForward();
    storage.StartMergeOperation()?.Join();
    Assert.That(SpinWait.SpinUntil(() => backup.CurrentGenerationId == 1, TimeSpan.FromSeconds(10)), Is.True);
    backup.Stop();
    backup.WaitForLiveBackup();
  }

  [Test]
  public async Task AStartedBackupUnderWritesDoesNotRunGenerationsBackToBack()
  {
    using (var cluster = Factory.CreateInProcess(Options(Data("source"), withOthers: false)))
    using (var maintainer = cluster.CreateMaintainer())
    using (var backup = cluster.CreateLiveBackup(BackupStorage(), new ClusterLiveBackupOptions { BackupAfterMerge = true }))
    {
      var domain = cluster.GetDomain(Domain);
      using var stop = new CancellationTokenSource();
      var writer = Task.Run(() =>
      {
        for (var key = 0L; !stop.IsCancellationRequested; ++key)
          Commit(domain, key);
      });
      backup.Start();
      // One merge starts one generation.
      var storage = cluster.GetCoordinatedColumnMaintenance<long, long>("left").ZoneTree.Maintenance;
      storage.MoveMutableSegmentForward();
      storage.StartMergeOperation()?.Join();
      Assert.That(SpinWait.SpinUntil(() => backup.CurrentGenerationId == 1, TimeSpan.FromSeconds(10)), Is.True);
      // A generation captures the columns in place, so it makes no merge that would start
      // the next one while the writes go on.
      await Task.Delay(1500);
      Assert.That(backup.CurrentGenerationId, Is.EqualTo(1));
      stop.Cancel();
      await writer;
      backup.Stop();
      backup.WaitForLiveBackup();
    }
    using var restored = await Factory.RestoreInProcessAsync(Options(Data("restored"), withOthers: false), BackupStorage());
    var restoredDomain = restored.GetDomain(Domain);
    Assert.That(Read(restoredDomain, "left"), Is.EqualTo(Read(restoredDomain, "right")));
  }

  [Test]
  public async Task ABackupNeedsEveryColumnOpenAndARestoreEveryDefinition()
  {
    using (Factory.CreateInProcess(Options(Data("source"))))
    {
    }
    // The raw and standalone columns are not declared, so this process does not open them.
    using (var cluster = Factory.OpenInProcess(Options(Data("source"), withOthers: false)))
    {
      using var backup = cluster.CreateLiveBackup(BackupStorage());
      var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await backup.CreateGenerationAsync());
      Assert.That(error.Message, Does.Contain("cache").And.Contain("visits"));
      cluster.GetRawColumn<long, long>("cache");
      cluster.GetStandaloneColumn<long, long>("visits");
      await backup.CreateGenerationAsync();
    }

    var missing = Assert.ThrowsAsync<ArgumentException>(async () =>
        await Factory.RestoreInProcessAsync(Options(Data("restored"), withOthers: false), BackupStorage()));
    Assert.That(missing.Message, Does.Contain("cache").And.Contain("visits"));
    Assert.That(Directory.Exists(Path.Combine(Data("restored"), "catalog")), Is.False);

    // A restore never writes over an existing cluster.
    Assert.ThrowsAsync<LiveBackupRestoreTargetAlreadyExistsException>(async () =>
        await Factory.RestoreInProcessAsync(Options(Data("source")), BackupStorage()));
    using var restored = await Factory.RestoreInProcessAsync(Options(Data("restored")), BackupStorage());
    Assert.That(restored.Maintenance.ColumnNames, Is.EquivalentTo(new[] { "left", "right", "cache", "visits" }));
  }
}
