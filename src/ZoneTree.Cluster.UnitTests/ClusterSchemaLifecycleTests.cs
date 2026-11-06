using ZoneTree.AbstractFileStream;
using System.Collections.Concurrent;
using System.Globalization;
using ZoneTree.Exceptions;

namespace ZoneTree.Cluster.UnitTests;

/// <summary>
/// Dropping and renaming columns and domains: what handles, views, transactions, iterators
/// and maintainers do around it, and the recovery of a drop a crash interrupted.
/// </summary>
public sealed class ClusterSchemaLifecycleTests
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
    DataDirectory = Path.Combine(root.FullName, ".tmp", "cluster-lifecycle", "tests", Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(DataDirectory))
      Directory.Delete(DataDirectory, recursive: true);
  }

  ZoneTreeClusterOptions Options(params ColumnDefinition[] columns) => new()
  {
    DataDirectory = DataDirectory,
    Logger = SilentTestLogger.Instance,
    Columns = columns
  };

  string ColumnDirectory(long id)
      => Path.Combine(DataDirectory, "columns", id.ToString(CultureInfo.InvariantCulture));

  [Test]
  public async Task DroppingEachKindRemovesItsNameItsFilesAndItsHandles()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var accounts = await cluster.Schema.CreateDomainAsync("accounts");
      var balances = await cluster.Schema.CreateColumnAsync("accounts", new CoordinatedColumnDefinition<int, int>("balances"));
      var visits = await cluster.Schema.CreateColumnAsync(new StandaloneColumnDefinition<int, int>("visits"));
      var cache = await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("cache"));
      using (var transaction = accounts.BeginTransaction())
      {
        transaction.GetColumn(balances).Upsert(1, 1);
        transaction.Commit();
      }
      visits.Execute(transaction => transaction.Upsert(1, 1));
      cache.Upsert(1, 1);

      Assert.That(await cluster.Schema.DropColumnAsync("balances"), Is.True);
      Assert.That(await cluster.Schema.DropColumnAsync("visits"), Is.True);
      Assert.That(await cluster.Schema.DropColumnAsync("cache"), Is.True);
      Assert.That(await cluster.Schema.DropColumnAsync("cache"), Is.False);

      Assert.That((await cluster.Schema.DescribeAsync()).Columns, Is.Empty);
      Assert.That(accounts.ColumnNames, Is.Empty);
      Assert.That(cluster.Maintenance.ColumnNames, Is.Empty);
      for (var id = 1; id <= 3; ++id)
        Assert.That(Directory.Exists(ColumnDirectory(id)), Is.False, $"column {id}");
      Assert.Throws<SchemaObjectDroppedException>(() => cache.Upsert(2, 2));
      Assert.Throws<SchemaObjectDroppedException>(() => visits.BeginTransaction());
      using (var transaction = accounts.BeginTransaction())
        Assert.Throws<SchemaObjectDroppedException>(() => transaction.GetColumn(balances));
      Assert.Throws<SchemaObjectNotFoundException>(() => cluster.GetRawColumn<int, int>("cache"));
    }
    using var reopened = Factory.OpenInProcess(Options());
    Assert.That(reopened.Maintenance.ColumnNames, Is.Empty);
    Assert.That(reopened.GetDomain("accounts").ColumnNames, Is.Empty);
  }

  [Test]
  public async Task ADroppedDeclaredColumnIsCreatedAgainEmptyOnTheNextOpen()
  {
    var options = Options(new RawColumnDefinition<int, int>("cache"));
    using (var cluster = Factory.CreateInProcess(options))
    {
      cluster.GetRawColumn<int, int>("cache").Upsert(1, 1);
      Assert.That(await cluster.Schema.DropColumnAsync("cache"), Is.True);
    }
    using var reopened = Factory.OpenInProcess(options);
    Assert.That(reopened.GetRawColumn<int, int>("cache").ContainsKey(1), Is.False);
  }

  [Test]
  public async Task ADropStopsRawOperationsAndEndsIterators()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var cache = await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("cache"));
    for (var key = 0; key < 100; ++key)
      cache.Upsert(key, key);
    var iterator = cache.CreateIterator();
    Assert.That(iterator.Next(), Is.True);
    var unexpected = new ConcurrentBag<Exception>();
    var writes = 0;
    var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
    {
      for (var key = writer * 1_000_000; ; ++key)
      {
        try
        {
          cache.Upsert(key, key);
          Interlocked.Increment(ref writes);
        }
        catch (SchemaObjectDroppedException) { return; }
        // A write that runs while the column closes fails like one on a disposed ZoneTree.
        catch (ObjectDisposedException) { return; }
        catch (Exception error)
        {
          unexpected.Add(error);
          return;
        }
      }
    })).ToArray();
    while (Volatile.Read(ref writes) < 1000)
      Thread.Yield();
    Assert.That(await cluster.Schema.DropColumnAsync("cache"), Is.True);
    await Task.WhenAll(writers);
    Assert.That(unexpected, Is.Empty);
    Assert.Throws<SchemaObjectDroppedException>(() => iterator.Next());
    iterator.Dispose();
    Assert.That(Directory.Exists(ColumnDirectory(1)), Is.False);
  }

  [Test]
  public async Task ADomainTransactionThatUsedADroppedColumnCommitsNoneOfItsWrites()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var accounts = await cluster.Schema.CreateDomainAsync("accounts");
    var first = await cluster.Schema.CreateColumnAsync("accounts", new CoordinatedColumnDefinition<int, int>("first"));
    var second = await cluster.Schema.CreateColumnAsync("accounts", new CoordinatedColumnDefinition<int, int>("second"));
    using (var transaction = accounts.BeginTransaction())
    {
      transaction.GetColumn(first).Upsert(1, 1);
      transaction.GetColumn(second).Upsert(1, 1);
      Assert.That(await cluster.Schema.DropColumnAsync("first"), Is.True);
      var error = Assert.Throws<SchemaObjectDroppedException>(() => transaction.Commit());
      Assert.That(error.Message, Does.Contain("first"));
      Assert.That(transaction.State, Is.EqualTo(Transactional.TransactionState.Aborted));
    }
    Assert.That(second.ContainsKey(1), Is.False);
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public async Task ReadsThroughAViewOfADroppedColumnFail()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var accounts = await cluster.Schema.CreateDomainAsync("accounts");
    var balances = await cluster.Schema.CreateColumnAsync("accounts", new CoordinatedColumnDefinition<int, int>("balances"));
    using var snapshot = accounts.BeginReadOnlyTransaction();
    var view = snapshot.GetColumn(balances);
    Assert.That(view.ContainsKey(1), Is.False);
    Assert.That(await cluster.Schema.DropColumnAsync("balances"), Is.True);
    Assert.Throws<SchemaObjectDroppedException>(() => view.ContainsKey(1));
  }

  [Test]
  public async Task ADropEndsTheDomainViewsThatOpenedIteratorsAndDeletesTheFiles()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var accounts = await cluster.Schema.CreateDomainAsync("accounts");
    var balances = await cluster.Schema.CreateColumnAsync("accounts", new CoordinatedColumnDefinition<int, int>("balances"));
    using (var setup = accounts.BeginTransaction())
    {
      for (var key = 0; key < 100; ++key)
        setup.GetColumn(balances).Upsert(key, key);
      setup.Commit();
    }
    // Moving the records to disk makes the iterators hold the segment files.
    using (var maintainer = cluster.CreateColumnMaintainer("balances"))
    {
      maintainer.EvictToDisk();
      await maintainer.WaitForBackgroundThreadsAsync();
    }
    using var snapshot = accounts.BeginReadOnlyTransaction();
    using var reader = snapshot.GetColumn(balances).CreateIterator();
    Assert.That(reader.Next(), Is.True);
    using var transaction = accounts.BeginTransaction();
    var view = transaction.GetColumn(balances);
    view.Upsert(1000, 1000);
    using var writer = view.CreateIterator();
    Assert.That(writer.Next(), Is.True);

    Assert.That(await cluster.Schema.DropColumnAsync("balances"), Is.True);
    // The drop ended both views, so their iterators released the files it deleted.
    Assert.That(Directory.Exists(ColumnDirectory(1)), Is.False);
    Assert.Throws<SchemaObjectDroppedException>(() => reader.Next());
    Assert.Throws<SchemaObjectDroppedException>(() => writer.Next());
    Assert.Throws<SchemaObjectDroppedException>(() => view.TryGet(1, out _));
    Assert.Throws<SchemaObjectDroppedException>(() => transaction.Commit());
    Assert.That(transaction.State, Is.EqualTo(Transactional.TransactionState.Aborted));
  }

  [Test]
  public async Task ADropEndsTheStandaloneTransactionsThatOpenedIteratorsAndDeletesTheFiles()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var visits = await cluster.Schema.CreateColumnAsync(new StandaloneColumnDefinition<int, int>("visits"));
    visits.Execute(transaction =>
    {
      for (var key = 0; key < 100; ++key)
        transaction.Upsert(key, key);
    });
    // Moving the records to disk makes the iterators hold the segment files.
    using (var maintainer = cluster.CreateColumnMaintainer("visits"))
    {
      maintainer.EvictToDisk();
      await maintainer.WaitForBackgroundThreadsAsync();
    }
    using var snapshot = visits.BeginReadOnlyTransaction();
    var reader = snapshot.CreateIterator();
    Assert.That(reader.Next(), Is.True);
    using var transaction = visits.BeginTransaction();
    transaction.Upsert(1000, 1000);
    var writer = transaction.CreateIterator();
    Assert.That(writer.Next(), Is.True);

    Assert.That(await cluster.Schema.DropColumnAsync("visits"), Is.True);
    // The drop ended both transactions, so their iterators released the files it deleted.
    Assert.That(Directory.Exists(ColumnDirectory(1)), Is.False);
    Assert.That(() => reader.Next(), Throws.Exception);
    Assert.That(() => writer.Next(), Throws.Exception);
    Assert.Throws<SchemaObjectDroppedException>(() => snapshot.ContainsKey(1));
    Assert.Throws<SchemaObjectDroppedException>(() => transaction.Commit());
    reader.Dispose();
    writer.Dispose();
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public async Task TheTransactionsOfADroppedStandaloneColumnFail()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var visits = await cluster.Schema.CreateColumnAsync(new StandaloneColumnDefinition<int, int>("visits"));
    var transaction = visits.BeginTransaction();
    transaction.Upsert(1, 1);
    var snapshot = visits.BeginReadOnlyTransaction();
    Assert.That(await cluster.Schema.DropColumnAsync("visits"), Is.True);
    Assert.Throws<SchemaObjectDroppedException>(() => transaction.Upsert(2, 2));
    Assert.Throws<SchemaObjectDroppedException>(() => transaction.Commit());
    Assert.ThrowsAsync<SchemaObjectDroppedException>(async () => await transaction.CommitDurableAsync());
    Assert.Throws<SchemaObjectDroppedException>(() => snapshot.ContainsKey(1));
    transaction.Dispose();
    snapshot.Dispose();
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public async Task ADroppedNameCanBeReusedButNeverItsId()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var old = await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("cache"));
    old.Upsert(1, 1);
    Assert.That(await cluster.Schema.DropColumnAsync("cache"), Is.True);
    var reused = await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, string>("cache"));
    Assert.That(reused.ContainsKey(1), Is.False);
    Assert.Throws<SchemaObjectDroppedException>(() => old.TryGet(1, out _));
    Assert.That(Directory.Exists(ColumnDirectory(1)), Is.False);
    Assert.That(Directory.Exists(ColumnDirectory(2)), Is.True);
  }

  [Test]
  public async Task ConcurrentDropsOfOneColumnDropItOnce()
  {
    using var cluster = Factory.CreateInProcess(Options());
    await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("cache"));
    var results = await Task.WhenAll(Enumerable.Range(0, 8)
        .Select(_ => cluster.Schema.DropColumnAsync("cache").AsTask()));
    Assert.That(results.Count(dropped => dropped), Is.EqualTo(1));
  }

  [Test]
  public async Task DropsRacingCreatesOfTheSameNameKeepTheCatalogConsistent()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var definition = new RawColumnDefinition<int, int>("cache");
    for (var round = 0; round < 20; ++round)
    {
      await cluster.Schema.GetOrCreateColumnAsync(definition);
      await Task.WhenAll(
          cluster.Schema.DropColumnAsync("cache").AsTask(),
          cluster.Schema.GetOrCreateColumnAsync(definition).AsTask());
      var columns = (await cluster.Schema.DescribeAsync()).Columns;
      Assert.That(columns, Has.Count.LessThanOrEqualTo(1));
      if (columns.Count == 1)
        cluster.GetRawColumn<int, int>("cache").Upsert(round, round);
    }
  }

  [Test]
  public async Task ADropACrashInterruptedIsFinishedWhenTheClusterOpens()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      (await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("kept"))).Upsert(1, 1);
      await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("files"));
    }
    // The catalog dropped both columns; a crash came before their storage was deleted.
    // The storage of the second one was deleted already.
    using (var catalog = ClusterCatalog.Open(DataDirectory, SilentTestLogger.Instance, new LocalFileStreamProvider(), canCreate: false, canOpen: true))
    {
      catalog.DropColumn(1);
      catalog.DropColumn(2);
    }
    Directory.Delete(ColumnDirectory(2), recursive: true);
    using (var reopened = Factory.OpenInProcess(Options()))
      Assert.That(reopened.Maintenance.ColumnNames, Is.Empty);
    Assert.That(Directory.Exists(ColumnDirectory(1)), Is.False);
    using var check = ClusterCatalog.Open(DataDirectory, SilentTestLogger.Instance, new LocalFileStreamProvider(), canCreate: false, canOpen: true);
    Assert.That(check.State.FindColumn(1), Is.Null);
    Assert.That(check.State.FindColumn(2), Is.Null);
  }

  [Test]
  public async Task DroppingAColumnThatThisProcessNeverOpenedDeletesIt()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
      (await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("cache"))).Upsert(1, 1);
    using var reopened = Factory.OpenInProcess(Options());
    Assert.That(await reopened.Schema.DropColumnAsync("cache"), Is.True);
    Assert.That(Directory.Exists(ColumnDirectory(1)), Is.False);
  }

  [Test]
  public async Task RenamingAColumnKeepsItsHandlesAndData()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var accounts = await cluster.Schema.CreateDomainAsync("accounts");
      var balances = await cluster.Schema.CreateColumnAsync("accounts", new CoordinatedColumnDefinition<int, int>("balances"));
      var cache = await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("cache"));
      cache.Upsert(1, 1);
      using var transaction = accounts.BeginTransaction();
      transaction.GetColumn(balances).Upsert(1, 1);
      await cluster.Schema.RenameColumnAsync("balances", "amounts");
      await cluster.Schema.RenameColumnAsync("cache", "store");
      transaction.Commit();
      Assert.That(balances.Name, Is.EqualTo("amounts"));
      Assert.That(accounts.GetColumn<int, int>("amounts"), Is.SameAs(balances));
      Assert.That(cluster.GetRawColumn<int, int>("store"), Is.SameAs(cache));
      Assert.Throws<SchemaObjectNotFoundException>(() => cluster.GetRawColumn<int, int>("cache"));
      Assert.ThrowsAsync<SchemaObjectAlreadyExistsException>(async () =>
          await cluster.Schema.RenameColumnAsync("store", "amounts"));
      Assert.ThrowsAsync<SchemaObjectNotFoundException>(async () =>
          await cluster.Schema.RenameColumnAsync("missing", "other"));
    }
    // A declaration of the old name creates a new column.
    using var reopened = Factory.OpenInProcess(Options(new RawColumnDefinition<int, int>("cache")));
    Assert.That(reopened.GetRawColumn<int, int>("store").TryGet(1, out var value) && value == 1, Is.True);
    Assert.That(reopened.GetDomain("accounts").GetColumn<int, int>("amounts").ContainsKey(1), Is.True);
    Assert.That(reopened.Maintenance.ColumnNames, Is.EqualTo(new[] { "amounts", "store", "cache" }));
  }

  [Test]
  public async Task DomainsAreDroppedOnlyWithoutColumnsAndCanBeRenamed()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var accounts = await cluster.Schema.CreateDomainAsync("accounts");
      var empty = await cluster.Schema.CreateDomainAsync("empty");
      await cluster.Schema.CreateColumnAsync("accounts", new CoordinatedColumnDefinition<int, int>("balances"));
      Assert.That(await cluster.Schema.DropDomainAsync("empty"), Is.True);
      Assert.That(await cluster.Schema.DropDomainAsync("empty"), Is.False);
      Assert.Throws<SchemaObjectDroppedException>(() => empty.BeginTransaction());
      Assert.ThrowsAsync<SchemaConflictException>(async () => await cluster.Schema.DropDomainAsync("accounts"));

      await cluster.Schema.RenameDomainAsync("accounts", "ledger");
      Assert.That(accounts.Name, Is.EqualTo("ledger"));
      Assert.That(cluster.GetDomain("ledger"), Is.SameAs(accounts));
      Assert.Throws<SchemaObjectNotFoundException>(() => cluster.GetDomain("accounts"));
      using (var transaction = accounts.BeginTransaction())
      {
        transaction.GetColumn(accounts.GetColumn<int, int>("balances")).Upsert(1, 1);
        transaction.Commit();
      }
    }
    using (var reopened = Factory.OpenInProcess(Options()))
    {
      Assert.That(reopened.Maintenance.DomainNames, Is.EqualTo(new[] { "ledger" }));
      Assert.That(reopened.GetDomain("ledger").GetColumn<int, int>("balances").ContainsKey(1), Is.True);
      Assert.That(await reopened.Schema.DropColumnAsync("balances"), Is.True);
      Assert.That(await reopened.Schema.DropDomainAsync("ledger"), Is.True);
    }
    using var empty2 = Factory.OpenInProcess(Options());
    Assert.That(empty2.Maintenance.DomainNames, Is.Empty);
  }

  [Test]
  public async Task MaintainersStopMaintainingADroppedColumn()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var cache = await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("cache"));
    var other = await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("other"));
    using var maintainer = cluster.CreateMaintainer();
    using var columnMaintainer = cluster.CreateColumnMaintainer("cache");
    cache.Upsert(1, 1);
    other.Upsert(1, 1);
    Assert.That(await cluster.Schema.DropColumnAsync("cache"), Is.True);
    maintainer.EvictToDisk();
    maintainer.WaitForBackgroundThreads();
    columnMaintainer.EvictToDisk();
    Assert.That(cluster.GetRawColumnMaintenance<int, int>("other").InMemoryRecordCount, Is.Zero);
  }

  [Test]
  public async Task AReadOnlyClusterRefusesDropsAndRenames()
  {
    using var cluster = Factory.CreateInProcess(new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      Domains = [new DomainDefinition("accounts")],
      Columns = [new RawColumnDefinition<int, int>("cache")]
    });
    cluster.IsReadOnly = true;
    Assert.ThrowsAsync<ZoneTreeIsReadOnlyException>(async () => await cluster.Schema.DropColumnAsync("cache"));
    Assert.ThrowsAsync<ZoneTreeIsReadOnlyException>(async () => await cluster.Schema.RenameColumnAsync("cache", "other"));
    Assert.ThrowsAsync<ZoneTreeIsReadOnlyException>(async () => await cluster.Schema.DropDomainAsync("accounts"));
    Assert.ThrowsAsync<ZoneTreeIsReadOnlyException>(async () => await cluster.Schema.RenameDomainAsync("accounts", "ledger"));
    cluster.IsReadOnly = false;
    Assert.That(await cluster.Schema.DropColumnAsync("cache"), Is.True);
  }
}
