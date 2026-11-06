using ZoneTree.Cluster;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Transactional;

namespace ZoneTree.Cluster.UnitTests;

/// <summary>
/// Failures during commits, syncs and catalog writes, and torn power losses: every acknowledged
/// durable commit survives, and every column keeps the same prefix of the domain's commits.
/// </summary>
public sealed class ClusterFaultTests
{
  const string Domain = "accounts";
  string DataDirectory;
  readonly ZoneTreeClusterFactory Factory = new();

  [SetUp]
  public void SetUp()
  {
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "ZoneTree.sln")))
      root = root.Parent;
    Assert.That(root, Is.Not.Null);
    DataDirectory = Path.Combine(root.FullName, ".tmp", "cluster-faults", "tests", Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(DataDirectory))
      Directory.Delete(DataDirectory, recursive: true);
  }

  ZoneTreeClusterOptions Options(PowerLossFileStreamProvider files,
      WriteAheadLogMode mode = WriteAheadLogMode.Shared, params string[] columns) => new()
  {
    DataDirectory = DataDirectory,
    Logger = SilentTestLogger.Instance,
    FileStreamProvider = files,
    CoordinatedWriteAheadLog = new() { WriteAheadLogMode = mode },
    Domains =
    [
      new DomainDefinition(Domain)
      {
        Columns = (columns.Length == 0 ? ["left", "right"] : columns)
            .Select(name => (CoordinatedColumnDefinition)new CoordinatedColumnDefinition<long, long>(name))
            .ToArray()
      }
    ]
  };

  static DomainCoordinator CoordinatorOf(IClusterDomain domain) => ((ClusterDomain)domain).Coordinator;

  static ITransactionalZoneTreeMaintenance<long, long> Storage(IZoneTreeClusterHost cluster, string column)
      => cluster.GetCoordinatedColumnMaintenance<long, long>(column);

  static void Commit(IClusterDomain domain, long key)
  {
    using var transaction = domain.BeginTransaction();
    transaction.GetColumn(domain.GetColumn<long, long>("left")).Upsert(key, key + 1);
    transaction.GetColumn(domain.GetColumn<long, long>("right")).Upsert(key, key + 1);
    transaction.Commit();
  }

  /// <summary>The keys of a column, which must hold the value one above the key.</summary>
  static long[] Keys(IClusterDomain domain, string column)
  {
    var keys = new List<long>();
    using var transaction = domain.BeginReadOnlyTransaction();
    using var iterator = transaction.GetColumn(domain.GetColumn<long, long>(column)).CreateIterator();
    while (iterator.Next())
    {
      Assert.That(iterator.CurrentValue, Is.EqualTo(iterator.CurrentKey + 1), $"{column} {iterator.CurrentKey}");
      keys.Add(iterator.CurrentKey);
    }
    return keys.ToArray();
  }

  static bool IsColumnFile(string path) => path.Contains("columns", StringComparison.Ordinal);

  static bool IsCatalogFile(string path) => path.Contains("catalog", StringComparison.Ordinal);

  [Test]
  public void ACommitRecordThatFailsInTheSecondColumnRollsBackBothAndStopsCommits()
  {
    var files = new PowerLossFileStreamProvider();
    using (var cluster = Factory.CreateInProcess(Options(files)))
    {
      var domain = cluster.GetDomain(Domain);
      Commit(domain, 1);
      using var failing = domain.BeginTransaction();
      failing.GetColumn(domain.GetColumn<long, long>("left")).Upsert(2, 3);
      failing.GetColumn(domain.GetColumn<long, long>("right")).Upsert(2, 3);
      // The left column writes its commit record first; the right one refuses its own.
      var right = Storage(cluster, "right").ZoneTree;
      right.IsReadOnly = true;
      Assert.Throws<ZoneTreeIsReadOnlyException>(() => failing.Commit());
      right.IsReadOnly = false;
      Assert.That(Keys(domain, "left"), Is.EqualTo(new long[] { 1 }));
      Assert.That(Keys(domain, "right"), Is.EqualTo(new long[] { 1 }));
      Assert.Throws<TransactionalTreeCommitsStoppedException>(() => Commit(domain, 3));
    }

    // The left column's commit record may be on the disk; recovery rolls it back.
    using var reopened = Factory.OpenInProcess(Options(files));
    var reopenedDomain = reopened.GetDomain(Domain);
    Assert.That(Keys(reopenedDomain, "left"), Is.EqualTo(new long[] { 1 }));
    Assert.That(Keys(reopenedDomain, "right"), Is.EqualTo(new long[] { 1 }));
    Commit(reopenedDomain, 4);
    Assert.That(Keys(reopenedDomain, "right"), Is.EqualTo(new long[] { 1, 4 }));
  }

  [Test]
  public async Task CommitsWaitingBehindAFailedCommitEndInDoubtAndRollBack()
  {
    var files = new PowerLossFileStreamProvider();
    using (var cluster = Factory.CreateInProcess(Options(files)))
    {
      var domain = cluster.GetDomain(Domain);
      Commit(domain, 1);
      // A commit takes its number, then fails before the next one becomes visible.
      var coordinator = CoordinatorOf(domain);
      var failed = coordinator.BeginCommit();
      using var waiting = domain.BeginTransaction();
      waiting.GetColumn(domain.GetColumn<long, long>("left")).Upsert(2, 3);
      waiting.GetColumn(domain.GetColumn<long, long>("right")).Upsert(2, 3);
      var commit = waiting.TryCommitAsync().AsTask();
      Assert.That(commit.IsCompleted, Is.False);
      coordinator.StopCommits(new IOException("Simulated commit record failure."), failed);
      Assert.ThrowsAsync<TransactionInDoubtException>(async () => await commit);
      Assert.That(waiting.State, Is.EqualTo(TransactionState.InDoubt));
      Assert.That(Keys(domain, "left"), Is.EqualTo(new long[] { 1 }));
      Assert.Throws<TransactionalTreeCommitsStoppedException>(() => Commit(domain, 3));
    }

    // The waiting commit wrote its records, but it follows a number no column holds.
    using var reopened = Factory.OpenInProcess(Options(files));
    var reopenedDomain = reopened.GetDomain(Domain);
    Assert.That(Keys(reopenedDomain, "left"), Is.EqualTo(new long[] { 1 }));
    Assert.That(Keys(reopenedDomain, "right"), Is.EqualTo(new long[] { 1 }));
    await Task.CompletedTask;
  }

  [TestCase(false)]
  [TestCase(true)]
  public void AFailedDomainSyncLeavesTheDurableCommitInDoubtAndEveryColumnAgrees(bool powerLoss)
  {
    var files = new PowerLossFileStreamProvider();
    var cluster = Factory.CreateInProcess(Options(files));
    var domain = cluster.GetDomain(Domain);
    using (var first = domain.BeginTransaction())
    {
      first.GetColumn(domain.GetColumn<long, long>("left")).Upsert(1, 2);
      first.GetColumn(domain.GetColumn<long, long>("right")).Upsert(1, 2);
      first.CommitDurableAsync().GetAwaiter().GetResult();
    }
    files.FailDiskSync = IsColumnFile;
    using (var durable = domain.BeginTransaction())
    {
      durable.GetColumn(domain.GetColumn<long, long>("left")).Upsert(2, 3);
      durable.GetColumn(domain.GetColumn<long, long>("right")).Upsert(2, 3);
      Assert.ThrowsAsync<TransactionInDoubtException>(async () => await durable.CommitDurableAsync());
      Assert.That(durable.State, Is.EqualTo(TransactionState.InDoubt));
    }
    files.FailDiskSync = null;
    // The domain's commits stopped, and the columns whose sync failed refuse new writes. Their
    // journals stay failed, so closing reports that.
    Assert.That(() => Commit(domain, 3), Throws.InstanceOf<ZoneTreeException>());
    var reopenFiles = powerLoss ? files.SimulatePowerLoss() : files;
    Assert.That(() => cluster.Dispose(), Throws.Exception);

    // After a process crash the records are there, so the commit survives in both columns.
    // After a power loss its syncs never completed, so it is gone from both.
    using var reopened = Factory.OpenInProcess(Options(reopenFiles));
    var reopenedDomain = reopened.GetDomain(Domain);
    var expected = powerLoss ? new long[] { 1 } : new long[] { 1, 2 };
    Assert.That(Keys(reopenedDomain, "left"), Is.EqualTo(expected));
    Assert.That(Keys(reopenedDomain, "right"), Is.EqualTo(expected));
  }

  [Test]
  public async Task AFailedCatalogWriteBeforeADropKeepsTheColumnForRecovery()
  {
    var files = new PowerLossFileStreamProvider();
    var cluster = Factory.CreateInProcess(Options(files));
    var domain = cluster.GetDomain(Domain);
    for (var key = 1L; key <= 3; ++key)
      Commit(domain, key);
    await cluster.SyncWALAsync();
    // An unsynced commit, so the drop's domain sync runs before the failing catalog write.
    Commit(domain, 4);
    // The drop records itself, syncs the domain, then fails to record the durable point.
    var columnsSynced = false;
    files.BeforeDiskSync = path => columnsSynced |= IsColumnFile(path);
    files.FailDiskSync = path => columnsSynced && IsCatalogFile(path);
    Assert.That(await cluster.Schema.DropColumnAsync("right"), Is.True);
    files.FailDiskSync = null;
    files.BeforeDiskSync = null;
    var dropped = ((ZoneTreeCluster)cluster).Catalog.State.DroppedColumns.Single();
    Assert.That(files.DirectoryExists(((ZoneTreeCluster)cluster).ColumnDirectory(dropped.Id)), Is.True);
    // The domain keeps committing. The catalog's journal stays failed, so the close is not
    // clean and reports that.
    using (var transaction = domain.BeginTransaction())
    {
      transaction.GetColumn(domain.GetColumn<long, long>("left")).Upsert(5, 6);
      transaction.Commit();
    }
    Assert.That(() => cluster.Dispose(), Throws.Exception);

    // After a power loss, the catalog holds no durable point for the drop. Closing the
    // dropped column synced its journal, and the attempt at a clean close synced the other.
    // Recovery counts the dropped column's commit records, so every commit is complete,
    // then purges the column. Without those records the commits would be incomplete.
    using var reopened = Factory.OpenInProcess(Options(files.SimulatePowerLoss(), WriteAheadLogMode.Shared, "left"));
    Assert.That(Keys(reopened.GetDomain(Domain), "left"), Is.EqualTo(new long[] { 1, 2, 3, 4, 5 }));
    Assert.That(reopened.Maintenance.ColumnNames, Is.EqualTo(new[] { "left" }));
    Assert.That(((ZoneTreeCluster)reopened).Catalog.State.DroppedColumns, Is.Empty);
  }

  [Test]
  public void AFailedCatalogWriteAtCloseLeavesTheDomainToRecovery()
  {
    var files = new PowerLossFileStreamProvider();
    using (var cluster = Factory.CreateInProcess(Options(files)))
    {
      var domain = cluster.GetDomain(Domain);
      for (var key = 1L; key <= 3; ++key)
        Commit(domain, key);
      files.FailDiskSync = IsCatalogFile;
      try { cluster.Dispose(); }
      catch (Exception) { /* Closing the catalog can report the failed sync too. */ }
      files.FailDiskSync = null;
    }

    using var reopened = Factory.OpenInProcess(Options(files));
    var reopenedDomain = reopened.GetDomain(Domain);
    Assert.That(Keys(reopenedDomain, "left"), Is.EqualTo(new long[] { 1, 2, 3 }));
    Assert.That(Keys(reopenedDomain, "right"), Is.EqualTo(new long[] { 1, 2, 3 }));
  }

  // Every power loss point of a workload, with torn sectors.

  [TestCase(WriteAheadLogMode.Shared, 1)]
  [TestCase(WriteAheadLogMode.Shared, 2)]
  [TestCase(WriteAheadLogMode.AsyncShared, 1)]
  [TestCase(WriteAheadLogMode.AsyncShared, 2)]
  public async Task TornPowerLossesKeepEveryDurableCommitAndOnePrefixInEveryColumn(
      WriteAheadLogMode mode, int seed)
  {
    const int count = 240;
    var random = new Random(seed);
    var workload = new PowerLossFileStreamProvider();
    var images = new List<(PowerLossFileStreamProvider Image, long Durable, string File)>();
    long durableCount = 0;
    using (var cluster = Factory.CreateInProcess(Options(workload, mode)))
    {
      var domain = cluster.GetDomain(Domain);
      var left = domain.GetColumn<long, long>("left");
      var right = domain.GetColumn<long, long>("right");
      workload.BeforeDiskSync = path =>
      {
        // Read the durable count first: the image holds at least those commits.
        var durable = Interlocked.Read(ref durableCount);
        PowerLossFileStreamProvider image;
        lock (random)
          image = workload.SimulateTornPowerLoss(random);
        lock (images)
          images.Add((image, durable, path));
      };
      for (var key = 0L; key < count; ++key)
      {
        using (var transaction = domain.BeginTransaction())
        {
          transaction.GetColumn(left).Upsert(key, key + 1);
          transaction.GetColumn(right).Upsert(key, key + 1);
          if (key % 8 == 7)
          {
            await transaction.CommitDurableAsync();
            Interlocked.Exchange(ref durableCount, key + 1);
          }
          else
            transaction.Commit();
        }
        // Merges move commit records out of the journals behind a durable watermark.
        if (key % 60 == 59)
        {
          var storage = Storage(cluster, key % 120 == 59 ? "left" : "right").ZoneTree;
          storage.Maintenance.MoveMutableSegmentForward();
          storage.Maintenance.StartMergeOperation()?.Join();
        }
      }
      workload.BeforeDiskSync = null;
    }

    List<(PowerLossFileStreamProvider Image, long Durable, string File)> snapshot;
    lock (images)
      snapshot = [.. images];
    Assert.That(snapshot.Count, Is.GreaterThan(40));
    var failures = new List<string>();
    foreach (var (image, durable, file) in snapshot)
    {
      var description = $"power loss in the sync of {file}, {durable} durable commits";
      try
      {
        using var cluster = Factory.OpenInProcess(Options(image, mode));
        var domain = cluster.GetDomain(Domain);
        var leftKeys = Keys(domain, "left");
        var rightKeys = Keys(domain, "right");
        // The commits are sequential, so what survives is a prefix, the same in every column.
        if (!leftKeys.SequenceEqual(rightKeys))
          failures.Add($"{description}: the columns differ.");
        else if (!leftKeys.SequenceEqual(Enumerable.Range(0, leftKeys.Length).Select(key => (long)key)))
          failures.Add($"{description}: the commits are not a prefix.");
        else if (leftKeys.Length < durable)
          failures.Add($"{description}: {durable - leftKeys.Length} durable commits are lost.");
        else
        {
          // The recovered domain commits again.
          Commit(domain, count);
          Assert.That(Keys(domain, "right"), Does.Contain((long)count));
        }
      }
      catch (Exception e)
      {
        failures.Add($"{description}: {e.GetType().Name}: {e.Message}");
      }
    }
    TestContext.Out.WriteLine($"Checked {snapshot.Count} torn images.");
    Assert.That(failures, Is.Empty, $"{failures.Count} of {snapshot.Count} images failed.");
  }
}
