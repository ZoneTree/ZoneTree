using System.Collections.Concurrent;
using ZoneTree.AbstractFileStream;
using ZoneTree.Cluster;
using ZoneTree.Exceptions;
using ZoneTree.Transactional;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Cluster.UnitTests;

/// <summary>
/// Transactions that commit atomically across the columns of a domain: one commit timestamp,
/// one snapshot, and recovery that keeps every transaction whole.
/// </summary>
public sealed class ClusterAtomicCommitTests
{
  const string Domain = "accounts";
  string DataDirectory;
  readonly ZoneTreeClusterFactory Factory = new();

  /// <summary>The file streams of the columns; the local file system when not set.</summary>
  IFileStreamProvider Files;

  [SetUp]
  public void SetUp()
  {
    Files = null;
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "ZoneTree.slnx")))
      root = root.Parent;
    Assert.That(root, Is.Not.Null);
    DataDirectory = Path.Combine(root.FullName, ".tmp", "cluster-atomic", "tests", Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(DataDirectory))
      Directory.Delete(DataDirectory, recursive: true);
  }

  ZoneTreeClusterOptions Options(params string[] columns) => new()
  {
    DataDirectory = DataDirectory,
    Logger = SilentTestLogger.Instance,
    FileStreamProvider = Files,
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

  /// <summary>Stops the domain's commits like a failed sync, so closing it is not clean.</summary>
  static void StopCommits(IClusterDomain domain)
      => CoordinatorOf(domain).FailWaiters(new IOException("Simulated sync failure."));

  [Test]
  public async Task ReadersSeeEveryTransactionWholeAndCommitsAreVisibleWhenTheyReturn()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var left = domain.GetColumn<long, long>("left");
    var right = domain.GetColumn<long, long>("right");
    var failures = new ConcurrentQueue<string>();
    var writing = 4;

    async Task Write(int writer)
    {
      try
      {
        var options = new ClusterTransactionOptions { IsolationLevel = TransactionIsolationLevel.Snapshot };
        for (var i = 1; i <= 150; ++i)
        {
          var key = (writer + i) % 8;
          var value = writer * 1_000_000L + i;
          while (true)
          {
            using var transaction = domain.BeginTransaction(options);
            transaction.GetColumn(left).Upsert(key, value);
            transaction.GetColumn(right).Upsert(key, value);
            // One writer commits durably, so ordinary commits also complete behind durable ones.
            var committed = writer == 0
                ? await transaction.TryCommitDurableAsync()
                : transaction.TryCommit();
            if (!committed)
              continue;
            if (domain.SnapshotTimestamp < transaction.CommitTimestamp)
              failures.Enqueue($"Commit {transaction.CommitTimestamp} returned before the snapshot reached it.");
            break;
          }
        }
      }
      finally
      {
        Interlocked.Decrement(ref writing);
      }
    }

    void Read()
    {
      while (Volatile.Read(ref writing) > 0)
      {
        using var transaction = domain.BeginReadOnlyTransaction();
        var leftView = transaction.GetColumn(left);
        var rightView = transaction.GetColumn(right);
        for (var key = 0L; key < 8; ++key)
        {
          var hasLeft = leftView.TryGet(key, out var leftValue);
          var hasRight = rightView.TryGet(key, out var rightValue);
          if (hasLeft != hasRight || leftValue != rightValue)
            failures.Enqueue($"Key {key} at snapshot {transaction.SnapshotTimestamp}: {leftValue} and {rightValue}.");
        }
      }
    }

    var tasks = Enumerable.Range(0, 4).Select(writer => Task.Run(() => Write(writer)))
        .Concat(Enumerable.Range(0, 2).Select(_ => Task.Run(Read)))
        .ToArray();
    await Task.WhenAll(tasks);
    Assert.That(failures, Is.Empty);
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [TestCase(TransactionIsolationLevel.Serializable, false)]
  [TestCase(TransactionIsolationLevel.Snapshot, true)]
  public void SerializableCommitsValidateReadsInColumnsTheyDidNotWrite(
      TransactionIsolationLevel isolation, bool writeSkewCommits)
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var left = domain.GetColumn<long, long>("left");
    var right = domain.GetColumn<long, long>("right");
    using (var setup = domain.BeginTransaction())
    {
      setup.GetColumn(left).Upsert(1, 1);
      setup.GetColumn(right).Upsert(1, 1);
      setup.Commit();
    }
    var options = new ClusterTransactionOptions { IsolationLevel = isolation };
    using var first = domain.BeginTransaction(options);
    using var second = domain.BeginTransaction(options);
    Assert.That(first.GetColumn(left).TryGet(1, out _), Is.True);
    Assert.That(second.GetColumn(right).TryGet(1, out _), Is.True);
    first.GetColumn(right).Upsert(1, 0);
    second.GetColumn(left).Upsert(1, 0);
    Assert.That(first.TryCommit(), Is.True);
    // The second read the right column, which the first wrote after the second's snapshot.
    Assert.That(second.TryCommit(), Is.EqualTo(writeSkewCommits));
    Assert.That(second.HasConflict, Is.EqualTo(!writeSkewCommits));
  }

  [TestCase(TransactionIsolationLevel.Snapshot)]
  [TestCase(TransactionIsolationLevel.Serializable)]
  public void AWriteThatFoundANewerCommitMakesTheCommitConflictEvenWithoutOtherWrites(
      TransactionIsolationLevel isolation)
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var left = domain.GetColumn<long, long>("left");
    var right = domain.GetColumn<long, long>("right");
    var options = new ClusterTransactionOptions { IsolationLevel = isolation };
    using var late = domain.BeginTransaction(options);
    using (var early = domain.BeginTransaction(options))
    {
      early.GetColumn(left).Upsert(1, 1);
      early.Commit();
    }
    // The write stores nothing, and the transaction's other view only read.
    late.GetColumn(left).Upsert(1, 2);
    late.GetColumn(right).TryGet(1, out _);
    Assert.That(late.TryCommit(), Is.False);
    Assert.That(late.HasConflict, Is.True);
    Assert.That(late.State, Is.EqualTo(TransactionState.Aborted));
    Assert.That(left.TryGet(1, out var value) && value == 1, Is.True);
  }

  [Test]
  public async Task RecoveryKeepsEveryCompleteCommitUpToTheFirstIncompleteOne()
  {
    var files = new PowerLossFileStreamProvider();
    Files = files;
    long durableId, ordinaryId, incompleteId, laterId;
    PowerLossFileStreamProvider afterPowerLoss;
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var domain = cluster.GetDomain(Domain);
      var left = domain.GetColumn<long, long>("left");
      var right = domain.GetColumn<long, long>("right");
      using (var durable = domain.BeginTransaction())
      {
        durable.GetColumn(left).Upsert(1, 1);
        durable.GetColumn(right).Upsert(1, 1);
        await durable.CommitDurableAsync();
        durableId = durable.TransactionId;
      }
      using (var ordinary = domain.BeginTransaction())
      {
        ordinary.GetColumn(left).Upsert(2, 2);
        ordinary.GetColumn(right).Upsert(2, 2);
        ordinary.Commit();
        ordinaryId = ordinary.TransactionId;
      }
      // The ordinary commit reaches the disk in both columns without a domain sync.
      await Storage(cluster, "left").ZoneTree.SyncWALAsync();
      await Storage(cluster, "right").ZoneTree.SyncWALAsync();
      using (var incomplete = domain.BeginTransaction())
      {
        incomplete.GetColumn(left).Upsert(3, 3);
        incomplete.GetColumn(right).Upsert(3, 3);
        incomplete.Commit();
        incompleteId = incomplete.TransactionId;
      }
      using (var later = domain.BeginTransaction())
      {
        later.GetColumn(right).Upsert(4, 4);
        later.Commit();
        laterId = later.TransactionId;
      }
      // Only the right column reaches the disk: the later commit is complete there, but it
      // follows a commit the left column lost.
      await Storage(cluster, "right").ZoneTree.SyncWALAsync();
      afterPowerLoss = files.SimulatePowerLoss();
      StopCommits(domain);
    }

    Files = afterPowerLoss;
    using (var reopened = Factory.OpenInProcess(Options()))
    {
      var domain = reopened.GetDomain(Domain);
      var left = domain.GetColumn<long, long>("left");
      var right = domain.GetColumn<long, long>("right");
      foreach (var key in new long[] { 1, 2 })
      {
        Assert.That(left.TryGet(key, out var value) && value == key, Is.True, $"left {key}");
        Assert.That(right.TryGet(key, out value) && value == key, Is.True, $"right {key}");
      }
      Assert.That(left.ContainsKey(3), Is.False);
      Assert.That(right.ContainsKey(3), Is.False);
      Assert.That(right.ContainsKey(4), Is.False);
      Assert.That(await domain.GetTransactionOutcomeAsync(durableId), Is.EqualTo(TransactionOutcome.Committed));
      Assert.That(await domain.GetTransactionOutcomeAsync(ordinaryId), Is.EqualTo(TransactionOutcome.Committed));
      Assert.That(await domain.GetTransactionOutcomeAsync(incompleteId), Is.Not.EqualTo(TransactionOutcome.Committed));
      Assert.That(await domain.GetTransactionOutcomeAsync(laterId), Is.Not.EqualTo(TransactionOutcome.Committed));

      // The domain commits again, and a clean close keeps the commits.
      using var next = domain.BeginTransaction();
      next.GetColumn(left).Upsert(5, 5);
      next.GetColumn(right).Upsert(5, 5);
      next.Commit();
    }

    using var clean = Factory.OpenInProcess(Options());
    var cleanDomain = clean.GetDomain(Domain);
    Assert.That(cleanDomain.GetColumn<long, long>("left").ContainsKey(5), Is.True);
    Assert.That(cleanDomain.GetColumn<long, long>("right").ContainsKey(5), Is.True);
    Assert.That(cleanDomain.GetColumn<long, long>("right").ContainsKey(3), Is.False);
    Assert.That(cleanDomain.GetColumn<long, long>("right").ContainsKey(4), Is.False);
  }

  [Test]
  public async Task RecoveryRollsBackACommitThatFollowsOneLostFromEveryColumn()
  {
    var files = new PowerLossFileStreamProvider();
    Files = files;
    long lostId, laterId;
    PowerLossFileStreamProvider afterPowerLoss;
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var domain = cluster.GetDomain(Domain);
      var left = domain.GetColumn<long, long>("left");
      var right = domain.GetColumn<long, long>("right");
      using (var durable = domain.BeginTransaction())
      {
        durable.GetColumn(left).Upsert(1, 1);
        durable.GetColumn(right).Upsert(1, 1);
        await durable.CommitDurableAsync();
      }
      // A deposit in the left column, which never reaches the disk.
      using (var lost = domain.BeginTransaction())
      {
        lost.GetColumn(left).Upsert(2, 100);
        lost.Commit();
        lostId = lost.TransactionId;
      }
      // A transfer that read the deposit and wrote only the right column, which does.
      using (var later = domain.BeginTransaction())
      {
        Assert.That(later.GetColumn(left).TryGet(2, out var amount), Is.True);
        later.GetColumn(right).Upsert(2, amount);
        later.Commit();
        laterId = later.TransactionId;
      }
      await Storage(cluster, "right").ZoneTree.SyncWALAsync();
      afterPowerLoss = files.SimulatePowerLoss();
      StopCommits(domain);
    }

    Files = afterPowerLoss;
    using var reopened = Factory.OpenInProcess(Options());
    var reopenedDomain = reopened.GetDomain(Domain);
    var reopenedLeft = reopenedDomain.GetColumn<long, long>("left");
    var reopenedRight = reopenedDomain.GetColumn<long, long>("right");
    Assert.That(reopenedLeft.ContainsKey(1) && reopenedRight.ContainsKey(1), Is.True);
    Assert.That(reopenedLeft.ContainsKey(2), Is.False);
    Assert.That(reopenedRight.ContainsKey(2), Is.False);
    Assert.That(await reopenedDomain.GetTransactionOutcomeAsync(lostId), Is.Not.EqualTo(TransactionOutcome.Committed));
    Assert.That(await reopenedDomain.GetTransactionOutcomeAsync(laterId), Is.Not.EqualTo(TransactionOutcome.Committed));
  }

  [Test]
  public void ACommitThatFailsAfterTakingItsNumberStopsTheDomainsCommits()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var domain = cluster.GetDomain(Domain);
      var left = domain.GetColumn<long, long>("left");
      var right = domain.GetColumn<long, long>("right");
      using (var first = domain.BeginTransaction())
      {
        first.GetColumn(left).Upsert(1, 1);
        first.GetColumn(right).Upsert(1, 1);
        first.Commit();
      }
      using var failing = domain.BeginTransaction();
      failing.GetColumn(left).Upsert(2, 2);
      failing.GetColumn(right).Upsert(2, 2);
      // The left column refuses the commit record, after the commit took its number.
      var storage = Storage(cluster, "left").ZoneTree;
      storage.IsReadOnly = true;
      Assert.Throws<ZoneTreeIsReadOnlyException>(() => failing.Commit());
      storage.IsReadOnly = false;
      using var next = domain.BeginTransaction();
      next.GetColumn(right).Upsert(3, 3);
      Assert.Throws<TransactionalTreeCommitsStoppedException>(() => next.Commit());
    }

    using (var reopened = Factory.OpenInProcess(Options()))
    {
      var domain = reopened.GetDomain(Domain);
      var left = domain.GetColumn<long, long>("left");
      var right = domain.GetColumn<long, long>("right");
      Assert.That(left.ContainsKey(1) && right.ContainsKey(1), Is.True);
      Assert.That(left.ContainsKey(2) || right.ContainsKey(2) || right.ContainsKey(3), Is.False);
      // The domain commits again after the reopen.
      using var again = domain.BeginTransaction();
      again.GetColumn(left).Upsert(4, 4);
      again.GetColumn(right).Upsert(4, 4);
      again.Commit();
    }
    using var clean = Factory.OpenInProcess(Options());
    Assert.That(clean.GetDomain(Domain).GetColumn<long, long>("right").ContainsKey(4), Is.True);
  }

  [Test]
  public async Task AColumnOpenedSessionsAfterAnUncleanShutdownStillRollsBackItsIncompleteCommits()
  {
    var files = new PowerLossFileStreamProvider();
    Files = files;
    PowerLossFileStreamProvider afterPowerLoss;
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var domain = cluster.GetDomain(Domain);
      using (var transaction = domain.BeginTransaction())
      {
        transaction.GetColumn(domain.GetColumn<long, long>("left")).Upsert(1, 1);
        transaction.GetColumn(domain.GetColumn<long, long>("right")).Upsert(1, 1);
        transaction.Commit();
      }
      // The commit reaches the disk only in the right column.
      await Storage(cluster, "right").ZoneTree.SyncWALAsync();
      afterPowerLoss = files.SimulatePowerLoss();
      StopCommits(domain);
    }
    // A session that never opens the right column still finds the commit incomplete.
    Files = afterPowerLoss;
    using (var cluster = Factory.OpenInProcess(Options("left")))
    {
      var domain = cluster.GetDomain(Domain);
      var left = domain.GetColumn<long, long>("left");
      Assert.That(left.ContainsKey(1), Is.False);
      using var transaction = domain.BeginTransaction();
      transaction.GetColumn(left).Upsert(2, 2);
      await transaction.CommitDurableAsync();
    }
    using var reopened = Factory.OpenInProcess(Options("left"));
    var right = reopened.GetDomain(Domain).GetColumn<long, long>("right");
    Assert.That(right.ContainsKey(1), Is.False);
    Assert.That(reopened.GetDomain(Domain).GetColumn<long, long>("left").ContainsKey(2), Is.True);
  }

  [Test]
  public async Task CompleteCommitsSurviveAnUncleanShutdownAfterAColumnMergedTheirRecords()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var domain = cluster.GetDomain(Domain);
      var left = domain.GetColumn<long, long>("left");
      var right = domain.GetColumn<long, long>("right");
      for (var key = 0L; key < 10; ++key)
      {
        using var transaction = domain.BeginTransaction();
        transaction.GetColumn(left).Upsert(key, key);
        transaction.GetColumn(right).Upsert(key, key);
        transaction.Commit();
      }
      // The merge moves the left column's commit records out of its journal, while the
      // right column's journal still holds theirs.
      var storage = Storage(cluster, "left").ZoneTree;
      storage.Maintenance.MoveMutableSegmentForward();
      storage.Maintenance.StartMergeOperation()?.Join();
      using (var transaction = domain.BeginTransaction())
      {
        transaction.GetColumn(left).Upsert(10, 10);
        transaction.GetColumn(right).Upsert(10, 10);
        transaction.Commit();
      }
      await Storage(cluster, "left").ZoneTree.SyncWALAsync();
      await Storage(cluster, "right").ZoneTree.SyncWALAsync();
      StopCommits(domain);
    }

    using var reopened = Factory.OpenInProcess(Options());
    var reopenedDomain = reopened.GetDomain(Domain);
    for (var key = 0L; key <= 10; ++key)
    {
      Assert.That(reopenedDomain.GetColumn<long, long>("left").TryGet(key, out var value) && value == key, Is.True, $"left {key}");
      Assert.That(reopenedDomain.GetColumn<long, long>("right").TryGet(key, out value) && value == key, Is.True, $"right {key}");
    }
  }

  [Test]
  public async Task CommitsWithADroppedColumnSurviveAnUncleanShutdown()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var domain = cluster.GetDomain(Domain);
      var left = domain.GetColumn<long, long>("left");
      var right = domain.GetColumn<long, long>("right");
      for (var key = 0L; key < 3; ++key)
      {
        using var transaction = domain.BeginTransaction();
        transaction.GetColumn(left).Upsert(key, key);
        transaction.GetColumn(right).Upsert(key, key);
        transaction.Commit();
      }
      // The left column keeps commit records whose other half leaves with the right column.
      Assert.That(await cluster.Schema.DropColumnAsync("right"), Is.True);
      StopCommits(domain);
    }

    using var reopened = Factory.OpenInProcess(Options("left"));
    var reopenedLeft = reopened.GetDomain(Domain).GetColumn<long, long>("left");
    for (var key = 0L; key < 3; ++key)
      Assert.That(reopenedLeft.TryGet(key, out var value) && value == key, Is.True, $"key {key}");
  }

  [Test]
  public async Task ADurableCommitSyncsEachColumnOnce()
  {
    var files = new PowerLossFileStreamProvider();
    Files = files;
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var left = domain.GetColumn<long, long>("left");
    var right = domain.GetColumn<long, long>("right");
    for (var key = 0L; key < 3; ++key)
    {
      using var transaction = domain.BeginTransaction();
      transaction.GetColumn(left).Upsert(key, key);
      transaction.GetColumn(right).Upsert(key, key);
      var before = files.DiskSyncCount;
      await transaction.CommitDurableAsync();
      Assert.That(files.DiskSyncCount - before, Is.EqualTo(2));
    }
  }

  [Test]
  public async Task OutcomesComeFromTheColumns()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var left = domain.GetColumn<long, long>("left");
    var right = domain.GetColumn<long, long>("right");
    long committedId;
    using (var committed = domain.BeginTransaction())
    {
      committed.GetColumn(left).Upsert(1, 1);
      committed.GetColumn(right).Upsert(1, 1);
      committed.Commit();
      committedId = committed.TransactionId;
    }
    // An older writer keeps the rolled back transaction's entry.
    using var older = domain.BeginTransaction();
    older.GetColumn(right).TryGet(1, out _);
    var pending = domain.BeginTransaction();
    pending.GetColumn(right).Upsert(2, 2);
    Assert.That(await domain.GetTransactionOutcomeAsync(committedId), Is.EqualTo(TransactionOutcome.Committed));
    Assert.That(await domain.GetTransactionOutcomeAsync(pending.TransactionId), Is.EqualTo(TransactionOutcome.Pending));
    pending.Rollback();
    Assert.That(await domain.GetTransactionOutcomeAsync(pending.TransactionId), Is.EqualTo(TransactionOutcome.Aborted));
    Assert.That(await domain.GetTransactionOutcomeAsync(older.TransactionId + 1_000_000), Is.EqualTo(TransactionOutcome.Unknown));
  }

  [Test]
  public void ManyCommitsWithoutSyncsStartADomainSync()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var left = domain.GetColumn<long, long>("left");
    for (var i = 0L; i < 4200; ++i)
    {
      using var transaction = domain.BeginTransaction();
      transaction.GetColumn(left).Upsert(i % 64, i);
      transaction.Commit();
    }
    var coordinator = CoordinatorOf(domain);
    Assert.That(SpinWait.SpinUntil(() => coordinator.DurableTimestamp > 0, TimeSpan.FromSeconds(10)), Is.True);
  }

  [Test]
  public void AMergeMakesTheCommitsDurableBeforeDroppingTheirRecords()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var left = domain.GetColumn<long, long>("left");
    var right = domain.GetColumn<long, long>("right");
    long Commit(long key)
    {
      using var transaction = domain.BeginTransaction();
      transaction.GetColumn(left).Upsert(key, key);
      transaction.GetColumn(right).Upsert(key, key);
      transaction.Commit();
      return transaction.CommitTimestamp;
    }
    var storage = Storage(cluster, "left").ZoneTree;
    void Merge()
    {
      storage.Maintenance.MoveMutableSegmentForward();
      storage.Maintenance.StartMergeOperation()?.Join();
    }
    int CountCommitRecords()
    {
      var count = 0;
      using var iterator = storage.CreateIterator();
      while (iterator.Next())
        if (iterator.CurrentKey.Kind == VersionedKeyKind.TransactionStatus)
          ++count;
      return count;
    }

    var coordinator = CoordinatorOf(domain);
    long last = 0;
    for (var key = 0L; key < 10; ++key)
      last = Commit(key);
    Assert.That(coordinator.DurableTimestamp, Is.LessThan(last));
    Merge();
    Assert.That(coordinator.DurableTimestamp, Is.GreaterThanOrEqualTo(last));
    Assert.That(CountCommitRecords(), Is.Zero);

    last = Commit(10);
    Merge();
    Assert.That(coordinator.DurableTimestamp, Is.GreaterThanOrEqualTo(last));
    Assert.That(CountCommitRecords(), Is.Zero);
    for (var key = 0L; key <= 10; ++key)
      Assert.That(left.TryGet(key, out var value) && value == key, Is.True, $"key {key}");
  }
}
