using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Transactional;
using ZoneTree.Cluster;

namespace ZoneTree.Cluster.UnitTests;

public sealed class ZoneTreeClusterTests
{
  string DataDirectory;
  readonly ZoneTreeClusterFactory Factory = new();

  [SetUp]
  public void SetUp()
  {
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "ZoneTree.sln")))
      root = root.Parent;
    Assert.That(root, Is.Not.Null);
    DataDirectory = Path.Combine(root.FullName, ".tmp", "cluster-slice", "tests", Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(DataDirectory))
      Directory.Delete(DataDirectory, recursive: true);
  }

  const string Domain = ClusterTestDomains.Name;

  ZoneTreeClusterOptions Options(string directory = null) => new()
  {
    DataDirectory = directory ?? DataDirectory,
    Logger = new ConsoleLogger(LogLevel.Error),
    Domains =
    [
      new DomainDefinition(Domain)
      {
        Columns =
        [
          new CoordinatedColumnDefinition<int, int>("numbers"),
          new CoordinatedColumnDefinition<long, string>("names"),
          new CoordinatedColumnDefinition<Guid, string>("events")
        ]
      }
    ]
  };

  [Test]
  public async Task AsyncOperationsOfOneViewRunConcurrently()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    var names = cluster.GetDomain(Domain).GetColumn<long, string>("names");
    using (var transaction = cluster.GetDomain(Domain).BeginTransaction())
    {
      // Operations on one view and across views run in parallel; none depends on another.
      var operations = new List<Task>();
      for (var i = 1; i <= 500; ++i)
      {
        var key = i;
        operations.Add(Task.Run(() => transaction.GetColumn(numbers).UpsertAsync(key, key).AsTask()));
        operations.Add(transaction.GetColumn(names).UpsertAsync(key, $"name {key}").AsTask());
        operations.Add(transaction.GetColumn(numbers).TryGetAsync(-key).AsTask());
      }
      await Task.WhenAll(operations);
      transaction.Commit();
    }
    for (var i = 1; i <= 500; ++i)
    {
      Assert.That(numbers.TryGet(i, out var value) && value == i, Is.True, $"number {i}");
      Assert.That(names.TryGet(i, out var name) && name == $"name {i}", Is.True, $"name {i}");
    }
  }

  [Test]
  public async Task CommitWaitsForRunningOperationsOfItsViews()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    var transaction = cluster.GetDomain(Domain).BeginTransaction();
    var view = transaction.GetColumn(numbers);
    var written = new System.Collections.Concurrent.ConcurrentBag<int>();
    using var started = new CountdownEvent(4);
    // Writers keep writing until the commit stops them; every write that returned commits.
    var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
    {
      started.Signal();
      for (var key = writer * 1_000_000; ; ++key)
      {
        try { view.Upsert(key, key); }
        catch (TransactionCommitInProgressException) { return; }
        catch (TransactionAlreadyCommittedException) { return; }
        written.Add(key);
      }
    })).ToArray();
    started.Wait();
    while (written.Count < 1000)
      Thread.Yield();
    transaction.Commit();
    await Task.WhenAll(writers);
    transaction.Dispose();
    using var read = cluster.GetDomain(Domain).BeginReadOnlyTransaction();
    var committed = read.GetColumn(numbers).Range(int.MinValue, int.MaxValue).Select(x => x.Key).ToHashSet();
    Assert.That(committed.SetEquals(written), Is.True);
  }

  [Test]
  public void ATransactionThatTimesOutIsRolledBack()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var numbers = domain.GetColumn<int, int>("numbers");
    var transaction = domain.BeginTransaction(
        new ClusterTransactionOptions { Timeout = TimeSpan.FromMilliseconds(100) });
    transaction.GetColumn(numbers).Upsert(1, 1);
    Assert.That(SpinWait.SpinUntil(() => transaction.State == TransactionState.Aborted, TimeSpan.FromSeconds(10)), Is.True);
    Assert.Throws<TransactionAbortedException>(() => transaction.GetColumn(numbers));
    Assert.Throws<TransactionAbortedException>(() => transaction.Commit());
    transaction.Dispose();
    Assert.That(numbers.TryGet(1, out _), Is.False);
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public void AReadOnlyTransactionThatTimesOutEnds()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var numbers = domain.GetColumn<int, int>("numbers");
    var snapshot = domain.BeginReadOnlyTransaction(
        new ClusterReadTransactionOptions { Timeout = TimeSpan.FromMilliseconds(100) });
    var view = snapshot.GetColumn(numbers);
    Assert.That(view.ContainsKey(1), Is.False);
    Assert.That(SpinWait.SpinUntil(() => cluster.Maintenance.ActiveTransactionCount == 0, TimeSpan.FromSeconds(10)), Is.True);
    Assert.Throws<ObjectDisposedException>(() => snapshot.GetColumn(numbers));
    Assert.Throws<ObjectDisposedException>(() => view.ContainsKey(1));
    snapshot.Dispose();
  }

  [Test]
  public void RetryHelpersApplyTheTransactionOptionsToEachAttempt()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var numbers = domain.GetColumn<int, int>("numbers");
    var options = new ClusterTransactionOptions
    {
      IsolationLevel = TransactionIsolationLevel.Snapshot,
      Timeout = TimeSpan.FromMilliseconds(100)
    };
    domain.Execute(transaction =>
    {
      Assert.That(transaction.IsolationLevel, Is.EqualTo(TransactionIsolationLevel.Snapshot));
      transaction.GetColumn(numbers).Upsert(1, 1);
    }, options);
    Assert.That(numbers.TryGet(1, out var value) && value == 1, Is.True);
    // An attempt that times out is not retried.
    var attempts = 0;
    Assert.Throws<TransactionAbortedException>(() => domain.Execute(transaction =>
    {
      ++attempts;
      transaction.GetColumn(numbers).Upsert(2, 2);
      SpinWait.SpinUntil(() => transaction.State == TransactionState.Aborted, TimeSpan.FromSeconds(10));
    }, options));
    Assert.That(attempts, Is.EqualTo(1));
    Assert.That(numbers.TryGet(2, out _), Is.False);
  }

  [Test]
  public void ATransactionThatEndsBeforeItsTimeoutKeepsItsOutcome()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var numbers = domain.GetColumn<int, int>("numbers");
    var options = new ClusterTransactionOptions
    {
      IsolationLevel = TransactionIsolationLevel.Snapshot,
      Timeout = TimeSpan.FromMilliseconds(100)
    };
    using (var transaction = domain.BeginTransaction(options))
    {
      Assert.That(transaction.IsolationLevel, Is.EqualTo(TransactionIsolationLevel.Snapshot));
      transaction.GetColumn(numbers).Upsert(1, 1);
      transaction.Commit();
      Thread.Sleep(300);
      Assert.That(transaction.State, Is.EqualTo(TransactionState.Committed));
    }
    Assert.That(numbers.TryGet(1, out var value) && value == 1, Is.True);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        domain.BeginTransaction(new ClusterTransactionOptions { Timeout = TimeSpan.FromSeconds(-1) }));
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public void CommitsTypedColumnsAndPreservesEachColumnSnapshot()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    var names = cluster.GetDomain(Domain).GetColumn<long, string>("names");
    using var old = cluster.GetDomain(Domain).BeginReadOnlyTransaction();
    var oldNumbers = old.GetColumn(numbers);
    Assert.That(oldNumbers.ContainsKey(1), Is.False);
    using (var transaction = cluster.GetDomain(Domain).BeginTransaction())
    {
      transaction.GetColumn(numbers).Upsert(1, 42);
      transaction.GetColumn(names).Upsert(1L, "Alice");
      Assert.That(transaction.GetColumn(numbers).TryGet(1, out var own), Is.True);
      Assert.That(own, Is.EqualTo(42));
      Assert.That(numbers.ContainsKey(1), Is.False);
      transaction.Commit();
      Assert.That(transaction.State, Is.EqualTo(TransactionState.Committed));
      Assert.Throws<TransactionAlreadyCommittedException>(() => transaction.GetColumn(numbers));
      Assert.That(transaction.CommitTimestamp, Is.GreaterThan(0));
    }
    Assert.That(numbers.TryGet(1, out var value), Is.True);
    Assert.That(value, Is.EqualTo(42));
    Assert.That(names.TryGet(1L, out var name), Is.True);
    Assert.That(name, Is.EqualTo("Alice"));
    Assert.That(oldNumbers.ContainsKey(1), Is.False);
  }

  [Test]
  public void DisposingAnActiveTransactionRollsBackEveryAccessedColumn()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    var names = cluster.GetDomain(Domain).GetColumn<long, string>("names");
    using (var transaction = cluster.GetDomain(Domain).BeginTransaction())
    {
      transaction.GetColumn(numbers).Upsert(1, 42);
      transaction.GetColumn(names).Upsert(1L, "Alice");
    }
    Assert.That(numbers.ContainsKey(1), Is.False);
    Assert.That(names.ContainsKey(1L), Is.False);
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [TestCase(false)]
  [TestCase(true)]
  public void AConflictRollsBackAllColumnsBeforeAnyCommit(bool reverseEnlistment)
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    var names = cluster.GetDomain(Domain).GetColumn<long, string>("names");
    using var loser = cluster.GetDomain(Domain).BeginTransaction();
    if (reverseEnlistment)
    {
      loser.GetColumn(names).Upsert(1L, "loser");
      loser.GetColumn(numbers).Upsert(1, 100);
    }
    else
    {
      loser.GetColumn(numbers).Upsert(1, 100);
      loser.GetColumn(names).Upsert(1L, "loser");
    }
    names.Upsert(1L, "winner");
    Assert.That(loser.TryCommit(), Is.False);
    Assert.That(loser.State, Is.EqualTo(TransactionState.Aborted));
    Assert.That(loser.HasConflict, Is.True);
    Assert.That(numbers.ContainsKey(1), Is.False);
    Assert.That(names.TryGet(1L, out var winner), Is.True);
    Assert.That(winner, Is.EqualTo("winner"));
  }

  [Test]
  public void SingleColumnConflictRemainsRetryable()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var column = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    using var loser = cluster.GetDomain(Domain).BeginTransaction();
    loser.GetColumn(column).Upsert(1, 10);
    column.Upsert(1, 20);
    Assert.That(loser.TryCommit(), Is.False);
    Assert.That(loser.State, Is.EqualTo(TransactionState.Aborted));
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
    Assert.That(column.TryGet(1, out var value), Is.True);
    Assert.That(value, Is.EqualTo(20));
  }

  [Test]
  public async Task ConflictingConcurrentCommitsDoNotDeadlockOrPartiallyCommit()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    var names = cluster.GetDomain(Domain).GetColumn<long, string>("names");
    using var ready = new Barrier(2);
    Task<bool> Write(int value, bool reverse) => Task.Run(() =>
    {
      using var transaction = cluster.GetDomain(Domain).BeginTransaction();
      if (reverse)
      {
        transaction.GetColumn(names).Upsert(1L, value.ToString());
        transaction.GetColumn(numbers).Upsert(1, value);
      }
      else
      {
        transaction.GetColumn(numbers).Upsert(1, value);
        transaction.GetColumn(names).Upsert(1L, value.ToString());
      }
      if (!ready.SignalAndWait(TimeSpan.FromSeconds(10)))
        throw new TimeoutException("Both writers did not reach the commit boundary.");
      return transaction.TryCommit();
    });
    var outcomes = await Task.WhenAll(Write(1, false), Write(2, true)).WaitAsync(TimeSpan.FromSeconds(20));
    Assert.That(outcomes.Count(committed => committed), Is.EqualTo(1));
    Assert.That(numbers.TryGet(1, out var number), Is.True);
    Assert.That(names.TryGet(1L, out var name), Is.True);
    Assert.That(name, Is.EqualTo(number.ToString()));
  }

  [Test]
  public async Task SeparateColumnViewsCanBeWrittenOnDifferentThreads()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    var names = cluster.GetDomain(Domain).GetColumn<long, string>("names");
    using var transaction = cluster.GetDomain(Domain).BeginTransaction();
    var amounts = transaction.GetColumn(numbers);
    var labels = transaction.GetColumn(names);
    using var ready = new Barrier(2);
    await Task.WhenAll(Task.Run(() =>
    {
      if (!ready.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
      for (var key = 0; key < 256; ++key) amounts.Upsert(key, key * 2);
    }), Task.Run(() =>
    {
      if (!ready.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
      for (var key = 0; key < 256; ++key) labels.Upsert(key, key.ToString());
    }));
    await transaction.CommitAsync();
    Assert.That(numbers.TryGet(255, out var number), Is.True);
    Assert.That(number, Is.EqualTo(510));
    Assert.That(names.TryGet(255L, out var name), Is.True);
    Assert.That(name, Is.EqualTo("255"));
  }

  [Test]
  public async Task AsyncConvenienceMethodsCommitAndReturnTypedResults()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    await numbers.UpsertAsync(1, 0);
    var found = await numbers.TryGetAsync(1);
    Assert.That(found.Found, Is.True);
    Assert.That(found.Value, Is.Zero);
    Assert.That(await numbers.AddOrUpdateAsync(1, 10, current => current + 3), Is.EqualTo(3));
    Assert.That(await numbers.TryDeleteAsync(1), Is.True);
    Assert.That(await numbers.TryDeleteAsync(1), Is.False);
    Assert.That((await numbers.TryGetAsync(1)).Found, Is.False);
    var value = await cluster.GetDomain(Domain).ExecuteDurableAsync(async (transaction, token) =>
    {
      var column = transaction.GetColumn(numbers);
      await column.UpsertAsync(2, 20, token);
      return (await column.TryGetForUpdateAsync(2, token)).Value;
    });
    Assert.That(value, Is.EqualTo(20));
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public void ASynchronousHelperRejectsABodyThatReturnsATask()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var numbers = domain.GetColumn<int, int>("numbers");
    async Task Write(IDomainTransaction transaction) => await transaction.GetColumn(numbers).UpsertAsync(1, 10);
    // The upsert would run on the thread pool after the commit.
    Assert.Throws<ArgumentException>(() => domain.Execute(transaction => transaction.GetColumn(numbers).UpsertAsync(1, 10)));
    Assert.Throws<ArgumentException>(() => domain.Execute(async transaction =>
    {
      await transaction.GetColumn(numbers).UpsertAsync(1, 10);
      return 1;
    }));
    // A lambda that returns the task without async binds to the synchronous body overload.
    Assert.ThrowsAsync<ArgumentException>(() => domain.ExecuteDurableAsync(transaction => Write(transaction)));
    Assert.That(numbers.TryGet(1, out _), Is.False);
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public async Task AsynchronousHelpersAwaitTheBodyBeforeTheCommit()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var numbers = domain.GetColumn<int, int>("numbers");
    async Task WriteTwo(IDomainTransaction transaction, int first)
    {
      var column = transaction.GetColumn(numbers);
      await column.UpsertAsync(first, first);
      await column.UpsertAsync(first + 1, first + 1);
    }
    await domain.ExecuteAsync(async transaction => await WriteTwo(transaction, 1));
    await domain.ExecuteDurableAsync(async transaction => await WriteTwo(transaction, 3));
    var result = await domain.ExecuteDurableAsync(async transaction =>
    {
      await WriteTwo(transaction, 5);
      return 7;
    });
    Assert.That(result, Is.EqualTo(7));
    for (var key = 1; key <= 6; ++key)
      Assert.That(numbers.TryGet(key, out var value) && value == key, Is.True, $"key {key}");
  }

  [Test]
  public async Task AsyncRangesAndIteratorsPreserveOrderingAndCancellation()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    cluster.GetDomain(Domain).Execute(transaction =>
    {
      var view = transaction.GetColumn(numbers);
      for (var key = 1; key <= 5; ++key) view.Upsert(key, key * 10);
      view.Delete(3);
    });
    await using var snapshot = await cluster.GetDomain(Domain).BeginReadOnlyTransactionAsync();
    var column = snapshot.GetColumn(numbers);
    var keys = new List<int>();
    await foreach (var record in column.RangeAsync(2, 5)) keys.Add(record.Key);
    Assert.That(keys, Is.EqualTo(new[] { 2, 4, 5 }));
    await using var iterator = column.CreateAsyncReverseIterator();
    await iterator.SeekAsync(4);
    keys.Clear();
    while (await iterator.NextAsync()) keys.Add(iterator.CurrentKey);
    Assert.That(keys, Is.EqualTo(new[] { 4, 2, 1 }));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await using var enumeration = column.RangeAsync(1, 5).GetAsyncEnumerator(cancellation.Token);
    Assert.ThrowsAsync<OperationCanceledException>(async () => await enumeration.MoveNextAsync());
    Assert.ThrowsAsync<OperationCanceledException>(async () => await column.TryGetAsync(1, cancellation.Token));
  }

  [Test]
  public void CreatesLocalTransactionsOnlyForAccessedColumnsAndCachesViews()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    var names = cluster.GetDomain(Domain).GetColumn<long, string>("names");
    using var transaction = cluster.GetDomain(Domain).BeginTransaction();
    Assert.That(cluster.GetCoordinatedColumnMaintenance<int, int>("numbers").ActiveTransactionCount, Is.Zero);
    Assert.That(cluster.GetCoordinatedColumnMaintenance<long, string>("names").ActiveTransactionCount, Is.Zero);
    var view = transaction.GetColumn(numbers);
    Assert.That(transaction.GetColumn(numbers), Is.SameAs(view));
    Assert.That(((IDomainReadTransaction)transaction).GetColumn(numbers), Is.SameAs(view));
    Assert.That(cluster.GetCoordinatedColumnMaintenance<int, int>("numbers").ActiveTransactionCount, Is.EqualTo(1));
    Assert.That(cluster.GetCoordinatedColumnMaintenance<long, string>("names").ActiveTransactionCount, Is.Zero);
    transaction.Rollback();
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
    Assert.Throws<TransactionAbortedException>(() => transaction.GetColumn(numbers));
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task CompletedWritesSurviveCleanReopen(bool durable)
  {
    var options = Options();
    using (var cluster = Factory.CreateInProcess(options))
    {
      using var transaction = cluster.GetDomain(Domain).BeginTransaction();
      transaction.GetColumn(cluster.GetDomain(Domain).GetColumn<int, int>("numbers")).Upsert(1, 42);
      transaction.GetColumn(cluster.GetDomain(Domain).GetColumn<long, string>("names")).Upsert(1L, "Alice");
      if (durable) await transaction.CommitDurableAsync();
      else transaction.Commit();
    }
    using var reopened = Factory.OpenInProcess(options);
    Assert.That(reopened.GetDomain(Domain).GetColumn<int, int>("numbers").TryGet(1, out var value), Is.True);
    Assert.That(value, Is.EqualTo(42));
    Assert.That(reopened.GetDomain(Domain).GetColumn<long, string>("names").TryGet(1L, out var name), Is.True);
    Assert.That(name, Is.EqualTo("Alice"));
    Assert.That(reopened.Maintenance.ColumnNames, Is.EqualTo(new[] { "numbers", "names", "events" }));
  }

  [Test]
  public void ValidatesSchemaAndColumnOwnership()
  {
    var options = Options();
    Assert.Throws<DatabaseNotFoundException>(() => Factory.OpenInProcess(options));
    using (var cluster = Factory.CreateInProcess(options))
    {
      Assert.Throws<IOException>(() => Factory.OpenInProcess(options));
      Assert.Throws<SchemaObjectNotFoundException>(() => cluster.GetDomain(Domain).GetColumn<int, int>("missing"));
      Assert.Throws<SchemaConflictException>(() => cluster.GetDomain(Domain).GetColumn<int, string>("numbers"));
      using var other = Factory.CreateInProcess(Options(Path.Combine(DataDirectory, "other")));
      using var transaction = cluster.GetDomain(Domain).BeginTransaction();
      Assert.Throws<ArgumentException>(() => transaction.GetColumn(other.GetDomain(Domain).GetColumn<int, int>("numbers")));
    }
    Assert.Throws<DatabaseAlreadyExistsException>(() => Factory.CreateInProcess(options));
    var mismatch = new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Domains = [new DomainDefinition(Domain) { Columns = [new CoordinatedColumnDefinition<int, string>("numbers")] }]
    };
    Assert.Throws<SchemaConflictException>(() => Factory.OpenOrCreateInProcess(mismatch));
    using var reopened = Factory.OpenInProcess(options);
    Assert.That(reopened.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public async Task ReadOnlyAdmissionCancellationAndLifetimeAreEnforced()
  {
    var cluster = Factory.CreateInProcess(Options());
    try
    {
      using var active = cluster.GetDomain(Domain).BeginTransaction();
      cluster.IsReadOnly = true;
      Assert.Throws<ZoneTreeIsReadOnlyException>(() => cluster.GetDomain(Domain).BeginTransaction());
      active.GetColumn(cluster.GetDomain(Domain).GetColumn<int, int>("numbers")).Upsert(1, 42);
      active.Commit();
      using var cancelled = new CancellationTokenSource();
      cancelled.Cancel();
      Assert.ThrowsAsync<OperationCanceledException>(async () => await cluster.GetDomain(Domain).BeginTransactionAsync(cancellationToken: cancelled.Token));
      await cluster.SyncWALAsync();
    }
    finally { cluster.Dispose(); }
    Assert.Throws<ObjectDisposedException>(() => cluster.GetDomain(Domain).BeginReadOnlyTransaction());
  }

  [Test]
  public void DisposingTheClusterRollsBackItsOpenTransactions()
  {
    var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var numbers = domain.GetColumn<int, int>("numbers");
    var names = domain.GetColumn<long, string>("names");
    using (var transaction = domain.BeginTransaction())
    {
      transaction.GetColumn(numbers).Upsert(1, 1);
      transaction.Commit();
    }
    var writer = domain.BeginTransaction();
    writer.GetColumn(numbers).Upsert(1, 42);
    writer.GetColumn(names).Upsert(1L, "Alice");
    var reader = domain.BeginReadOnlyTransaction();
    Assert.That(reader.GetColumn(numbers).TryGet(1, out var seen) && seen == 1, Is.True);
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.EqualTo(2));

    cluster.Dispose();
    Assert.That(writer.State, Is.EqualTo(TransactionState.Aborted));
    Assert.Throws<TransactionAbortedException>(() => writer.GetColumn(numbers));
    Assert.Throws<ObjectDisposedException>(() => reader.GetColumn(numbers));
    writer.Dispose();
    reader.Dispose();

    using var reopened = Factory.OpenInProcess(Options());
    var reopenedDomain = reopened.GetDomain(Domain);
    Assert.That(reopenedDomain.GetColumn<int, int>("numbers").TryGet(1, out var value) && value == 1, Is.True);
    Assert.That(reopenedDomain.GetColumn<long, string>("names").ContainsKey(1L), Is.False);
    Assert.That(reopened.Maintenance.AbortedTransactionCount, Is.Zero);
  }

  [Test]
  public void AColumnFailureDuringCommitRollsBackEveryColumn()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = cluster.GetDomain(Domain);
    var numbers = domain.GetColumn<int, int>("numbers");
    var names = domain.GetColumn<long, string>("names");
    using var transaction = domain.BeginTransaction();
    transaction.GetColumn(numbers).Upsert(1, 42);
    transaction.GetColumn(names).Upsert(1L, "Alice");
    var namesTree = cluster.GetCoordinatedColumnMaintenance<long, string>("names").ZoneTree;
    namesTree.IsReadOnly = true;
    // The failure is not a conflict, and no column keeps the transaction's writes.
    Assert.Throws<ZoneTreeIsReadOnlyException>(() => transaction.TryCommit());
    Assert.That(transaction.State, Is.EqualTo(TransactionState.Aborted));
    Assert.That(transaction.HasConflict, Is.False);
    Assert.Throws<TransactionAbortedException>(() => transaction.GetColumn(numbers));
    Assert.That(numbers.ContainsKey(1), Is.False);
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
    namesTree.IsReadOnly = false;
    Assert.That(names.ContainsKey(1L), Is.False);

    // The commit failed after it took its sequence number, so the domain's commits stop
    // until the cluster reopens; recovery cuts at that number.
    using var next = domain.BeginTransaction();
    next.GetColumn(numbers).Upsert(2, 2);
    Assert.Throws<TransactionalTreeCommitsStoppedException>(() => next.Commit());
    Assert.That(numbers.ContainsKey(2), Is.False);
  }

  [Test]
  public async Task MaintenanceCanMergeColumnsAndTheDatabaseCanBeDropped()
  {
    var options = Options();
    using var cluster = Factory.CreateInProcess(options);
    var numbers = cluster.GetDomain(Domain).GetColumn<int, int>("numbers");
    numbers.Upsert(1, 42);
    using (var maintainer = cluster.CreateMaintainer())
    {
      maintainer.EnableJobForCleaningInactiveCaches = false;
      maintainer.EvictToDisk();
      await maintainer.WaitForBackgroundThreadsAsync();
    }
    cluster.Maintenance.SaveMetaData();
    Assert.That(numbers.TryGet(1, out var value), Is.True);
    Assert.That(value, Is.EqualTo(42));
    cluster.Maintenance.Drop();
    Assert.Throws<DatabaseNotFoundException>(() => Factory.OpenInProcess(options));
    using var recreated = Factory.CreateInProcess(options);
    Assert.That(recreated.GetDomain(Domain).GetColumn<int, int>("numbers").ContainsKey(1), Is.False);
  }
}
