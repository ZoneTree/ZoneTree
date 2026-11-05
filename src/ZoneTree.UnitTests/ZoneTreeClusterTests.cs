using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Transactional;
using ZoneTree.Cluster;

namespace ZoneTree.UnitTests;

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

  ZoneTreeClusterOptions Options(string directory = null) => new()
  {
    DataDirectory = directory ?? DataDirectory,
    Logger = new ConsoleLogger(LogLevel.Error),
    Columns =
    [
      new CoordinatedColumnDefinition<int, int>("numbers"),
      new CoordinatedColumnDefinition<long, string>("names"),
      new CoordinatedColumnDefinition<Guid, string>("events")
    ]
  };

  [Test]
  public async Task AsyncColumnOperationsKeepTheirCallOrderInEachColumn()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetCoordinatedColumn<int, int>("numbers");
    var names = cluster.GetCoordinatedColumn<long, string>("names");
    using (var transaction = cluster.BeginTransaction())
    {
      var numberView = transaction.GetColumn(numbers);
      var nameView = transaction.GetColumn(names);
      // The columns run in parallel, the operations of each column in call order.
      var writes = new List<Task>();
      for (var i = 1; i <= 100; ++i)
      {
        writes.Add(numberView.UpsertAsync(1, i).AsTask());
        writes.Add(nameView.UpsertAsync(i, $"name {i}").AsTask());
      }
      await Task.WhenAll(writes);
      var own = await numberView.TryGetAsync(1);
      Assert.That(own.Found && own.Value == 100, Is.True);
      transaction.Commit();
    }
    Assert.That(numbers.TryGet(1, out var value) && value == 100, Is.True);
    for (var i = 1L; i <= 100; ++i)
      Assert.That(names.TryGet(i, out var name) && name == $"name {i}", Is.True, $"key {i}");
  }

  [Test]
  public void CommitsTypedColumnsAndPreservesEachColumnSnapshot()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetCoordinatedColumn<int, int>("numbers");
    var names = cluster.GetCoordinatedColumn<long, string>("names");
    using var old = cluster.BeginReadOnlyTransaction();
    var oldNumbers = old.GetColumn(numbers);
    Assert.That(oldNumbers.ContainsKey(1), Is.False);
    using (var transaction = cluster.BeginTransaction())
    {
      transaction.GetColumn(numbers).Upsert(1, 42);
      transaction.GetColumn(names).Upsert(1L, "Alice");
      Assert.That(transaction.GetColumn(numbers).TryGet(1, out var own), Is.True);
      Assert.That(own, Is.EqualTo(42));
      Assert.That(numbers.ContainsKey(1), Is.False);
      transaction.Commit();
      Assert.That(transaction.State, Is.EqualTo(TransactionState.Committed));
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
    var numbers = cluster.GetCoordinatedColumn<int, int>("numbers");
    var names = cluster.GetCoordinatedColumn<long, string>("names");
    using (var transaction = cluster.BeginTransaction())
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
    var numbers = cluster.GetCoordinatedColumn<int, int>("numbers");
    var names = cluster.GetCoordinatedColumn<long, string>("names");
    using var loser = cluster.BeginTransaction();
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
    var column = cluster.GetCoordinatedColumn<int, int>("numbers");
    using var loser = cluster.BeginTransaction();
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
    var numbers = cluster.GetCoordinatedColumn<int, int>("numbers");
    var names = cluster.GetCoordinatedColumn<long, string>("names");
    using var ready = new Barrier(2);
    Task<bool> Write(int value, bool reverse) => Task.Run(() =>
    {
      using var transaction = cluster.BeginTransaction();
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
    var numbers = cluster.GetCoordinatedColumn<int, int>("numbers");
    var names = cluster.GetCoordinatedColumn<long, string>("names");
    using var transaction = cluster.BeginTransaction();
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
    var numbers = cluster.GetCoordinatedColumn<int, int>("numbers");
    await numbers.UpsertAsync(1, 0);
    var found = await numbers.TryGetAsync(1);
    Assert.That(found.Found, Is.True);
    Assert.That(found.Value, Is.Zero);
    Assert.That(await numbers.AddOrUpdateAsync(1, 10, current => current + 3), Is.EqualTo(3));
    Assert.That(await numbers.TryDeleteAsync(1), Is.True);
    Assert.That(await numbers.TryDeleteAsync(1), Is.False);
    Assert.That((await numbers.TryGetAsync(1)).Found, Is.False);
    var value = await cluster.ExecuteDurableAsync(async (transaction, token) =>
    {
      var column = transaction.GetColumn(numbers);
      await column.UpsertAsync(2, 20, token);
      return (await column.TryGetForUpdateAsync(2, token)).Value;
    });
    Assert.That(value, Is.EqualTo(20));
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public async Task AsyncRangesAndIteratorsPreserveOrderingAndCancellation()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetCoordinatedColumn<int, int>("numbers");
    cluster.Execute(transaction =>
    {
      var view = transaction.GetColumn(numbers);
      for (var key = 1; key <= 5; ++key) view.Upsert(key, key * 10);
      view.Delete(3);
    });
    await using var snapshot = await cluster.BeginReadOnlyTransactionAsync();
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
    var numbers = cluster.GetCoordinatedColumn<int, int>("numbers");
    var names = cluster.GetCoordinatedColumn<long, string>("names");
    using var transaction = cluster.BeginTransaction();
    Assert.That(numbers.Maintenance.ActiveTransactionCount, Is.Zero);
    Assert.That(names.Maintenance.ActiveTransactionCount, Is.Zero);
    var view = transaction.GetColumn(numbers);
    Assert.That(transaction.GetColumn(numbers), Is.SameAs(view));
    Assert.That(((IClusterReadTransaction)transaction).GetColumn(numbers), Is.SameAs(view));
    Assert.That(numbers.Maintenance.ActiveTransactionCount, Is.EqualTo(1));
    Assert.That(names.Maintenance.ActiveTransactionCount, Is.Zero);
    transaction.Rollback();
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
    Assert.Throws<InvalidOperationException>(() => transaction.GetColumn(numbers));
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task CompletedWritesSurviveCleanReopen(bool durable)
  {
    var options = Options();
    using (var cluster = Factory.CreateInProcess(options))
    {
      using var transaction = cluster.BeginTransaction();
      transaction.GetColumn(cluster.GetCoordinatedColumn<int, int>("numbers")).Upsert(1, 42);
      transaction.GetColumn(cluster.GetCoordinatedColumn<long, string>("names")).Upsert(1L, "Alice");
      if (durable) await transaction.CommitDurableAsync();
      else transaction.Commit();
    }
    using var reopened = Factory.OpenInProcess(options);
    Assert.That(reopened.GetCoordinatedColumn<int, int>("numbers").TryGet(1, out var value), Is.True);
    Assert.That(value, Is.EqualTo(42));
    Assert.That(reopened.GetCoordinatedColumn<long, string>("names").TryGet(1L, out var name), Is.True);
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
      Assert.Throws<KeyNotFoundException>(() => cluster.GetCoordinatedColumn<int, int>("missing"));
      Assert.Throws<InvalidOperationException>(() => cluster.GetCoordinatedColumn<int, string>("numbers"));
      using var other = Factory.CreateInProcess(Options(Path.Combine(DataDirectory, "other")));
      using var transaction = cluster.BeginTransaction();
      Assert.Throws<ArgumentException>(() => transaction.GetColumn(other.GetCoordinatedColumn<int, int>("numbers")));
    }
    Assert.Throws<DatabaseAlreadyExistsException>(() => Factory.CreateInProcess(options));
    var mismatch = new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Columns = [new CoordinatedColumnDefinition<int, string>("numbers")]
    };
    Assert.Throws<InvalidDataException>(() => Factory.OpenOrCreateInProcess(mismatch));
    using var reopened = Factory.OpenInProcess(options);
    Assert.That(reopened.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public async Task ReadOnlyAdmissionCancellationAndLifetimeAreEnforced()
  {
    var cluster = Factory.CreateInProcess(Options());
    try
    {
      using var active = cluster.BeginTransaction();
      cluster.IsReadOnly = true;
      Assert.Throws<ZoneTreeIsReadOnlyException>(() => cluster.BeginTransaction());
      active.GetColumn(cluster.GetCoordinatedColumn<int, int>("numbers")).Upsert(1, 42);
      active.Commit();
      using var cancelled = new CancellationTokenSource();
      cancelled.Cancel();
      Assert.ThrowsAsync<OperationCanceledException>(async () => await cluster.BeginTransactionAsync(cancellationToken: cancelled.Token));
      await using (var snapshot = await cluster.BeginReadOnlyTransactionAsync())
        Assert.Throws<InvalidOperationException>(() => cluster.Dispose());
      await cluster.SyncWALAsync();
    }
    finally { cluster.Dispose(); }
    Assert.Throws<ObjectDisposedException>(() => cluster.BeginReadOnlyTransaction());
  }

  [Test]
  public void AColumnFailureAfterCommitStartsIsNotReportedAsARetryableConflict()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var numbers = cluster.GetCoordinatedColumn<int, int>("numbers");
    var names = cluster.GetCoordinatedColumn<long, string>("names");
    using var transaction = cluster.BeginTransaction();
    transaction.GetColumn(numbers).Upsert(1, 42);
    transaction.GetColumn(names).Upsert(1L, "Alice");
    names.Maintenance.ZoneTree.IsReadOnly = true;
    Assert.Throws<ZoneTreeIsReadOnlyException>(() => transaction.TryCommit());
    Assert.That(transaction.State, Is.EqualTo(TransactionState.InDoubt));
    Assert.That(numbers.TryGet(1, out var value), Is.True);
    Assert.That(value, Is.EqualTo(42));
    Assert.That(names.ContainsKey(1L), Is.False);
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public async Task MaintenanceCanMergeColumnsAndTheDatabaseCanBeDropped()
  {
    var options = Options();
    using var cluster = Factory.CreateInProcess(options);
    var numbers = cluster.GetCoordinatedColumn<int, int>("numbers");
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
    Assert.That(recreated.GetCoordinatedColumn<int, int>("numbers").ContainsKey(1), Is.False);
  }
}
