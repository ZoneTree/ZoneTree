using System.Collections.Concurrent;
using ZoneTree.Cluster;
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

  [SetUp]
  public void SetUp()
  {
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "ZoneTree.sln")))
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
      => CoordinatorOf(domain).StopCommits(new IOException("Simulated sync failure."));

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
  public async Task RecoveryRollsBackEveryCommitAboveTheDurableTimestampInEveryColumn()
  {
    long durableId, ordinaryId, durableTimestamp;
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
        durableTimestamp = durable.CommitTimestamp;
      }
      Assert.That(CoordinatorOf(domain).DurableTimestamp, Is.GreaterThanOrEqualTo(durableTimestamp));
      using (var ordinary = domain.BeginTransaction())
      {
        ordinary.GetColumn(left).Upsert(2, 2);
        ordinary.GetColumn(right).Upsert(2, 2);
        ordinary.Commit();
        ordinaryId = ordinary.TransactionId;
      }
      // The ordinary commit's records reach the disk, but the domain never recorded them as durable.
      await Storage(cluster, "left").ZoneTree.SyncWALAsync();
      await Storage(cluster, "right").ZoneTree.SyncWALAsync();
      Assert.That(left.TryGet(2, out _), Is.True);
      StopCommits(domain);
    }

    using (var reopened = Factory.OpenInProcess(Options()))
    {
      var domain = reopened.GetDomain(Domain);
      var left = domain.GetColumn<long, long>("left");
      var right = domain.GetColumn<long, long>("right");
      Assert.That(left.TryGet(1, out var value) && value == 1, Is.True);
      Assert.That(right.TryGet(1, out value) && value == 1, Is.True);
      Assert.That(left.ContainsKey(2), Is.False);
      Assert.That(right.ContainsKey(2), Is.False);
      Assert.That(await domain.GetTransactionOutcomeAsync(durableId), Is.EqualTo(TransactionOutcome.Committed));
      Assert.That(await domain.GetTransactionOutcomeAsync(ordinaryId), Is.EqualTo(TransactionOutcome.Aborted));

      // The domain commits again, and a clean close keeps the commits.
      using var next = domain.BeginTransaction();
      next.GetColumn(left).Upsert(3, 3);
      next.GetColumn(right).Upsert(3, 3);
      next.Commit();
    }

    using var clean = Factory.OpenInProcess(Options());
    var cleanDomain = clean.GetDomain(Domain);
    Assert.That(cleanDomain.GetColumn<long, long>("left").ContainsKey(3), Is.True);
    Assert.That(cleanDomain.GetColumn<long, long>("right").ContainsKey(3), Is.True);
    Assert.That(cleanDomain.GetColumn<long, long>("left").ContainsKey(2), Is.False);
  }

  [Test]
  public async Task AColumnOpenedSessionsAfterAnUncleanShutdownStillRollsBackItsCommits()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var domain = cluster.GetDomain(Domain);
      using var transaction = domain.BeginTransaction();
      transaction.GetColumn(domain.GetColumn<long, long>("left")).Upsert(1, 1);
      transaction.GetColumn(domain.GetColumn<long, long>("right")).Upsert(1, 1);
      transaction.Commit();
      await Storage(cluster, "right").ZoneTree.SyncWALAsync();
      StopCommits(domain);
    }
    // A session that never opens the right column moves the durable timestamp past the commit.
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
  public async Task MergesDropCommitRecordsOnlyOnceTheDomainMadeThemDurable()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var left = domain.GetColumn<long, long>("left");
    var right = domain.GetColumn<long, long>("right");
    void Commit(long key)
    {
      using var transaction = domain.BeginTransaction();
      transaction.GetColumn(left).Upsert(key, key);
      transaction.GetColumn(right).Upsert(key, key);
      transaction.Commit();
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

    for (var key = 0L; key < 10; ++key)
      Commit(key);
    Merge();
    Assert.That(CountCommitRecords(), Is.EqualTo(10));

    await cluster.SyncWALAsync();
    Commit(10);
    Merge();
    Assert.That(CountCommitRecords(), Is.EqualTo(1));
    for (var key = 0L; key <= 10; ++key)
      Assert.That(left.TryGet(key, out var value) && value == key, Is.True, $"key {key}");
  }
}
