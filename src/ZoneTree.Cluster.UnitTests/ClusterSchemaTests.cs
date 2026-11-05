using ZoneTree.Exceptions;
using ZoneTree.Serializers;

namespace ZoneTree.Cluster.UnitTests;

/// <summary>
/// The cluster catalog as the source of truth: schema changes at run time, declarations,
/// encodings, durable domain timestamps and the recovery of interrupted column creation.
/// </summary>
public sealed class ClusterSchemaTests
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
    DataDirectory = Path.Combine(root.FullName, ".tmp", "cluster-schema", "tests", Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(DataDirectory))
      Directory.Delete(DataDirectory, recursive: true);
  }

  ZoneTreeClusterOptions Options(ClusterEncodingRegistry encodings = null) => new()
  {
    DataDirectory = DataDirectory,
    Logger = SilentTestLogger.Instance,
    Encodings = encodings ?? new ClusterEncodingRegistry()
  };

  [Test]
  public async Task ColumnsCreatedAtRunTimeWorkAndOpenOnFirstUseAfterReopen()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var accounts = await cluster.Schema.CreateDomainAsync("accounts");
      var balances = await cluster.Schema.CreateColumnAsync("accounts", new CoordinatedColumnDefinition<long, long>("balances"));
      var names = await cluster.Schema.CreateColumnAsync("accounts", new CoordinatedColumnDefinition<long, string>("names"));
      var visits = await cluster.Schema.CreateColumnAsync(new StandaloneColumnDefinition<int, int>("visits"));
      var cache = await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<string, string>("cache"));
      Assert.That(accounts.ColumnNames, Is.EqualTo(new[] { "balances", "names" }));
      using (var transaction = accounts.BeginTransaction())
      {
        transaction.GetColumn(balances).Upsert(1, 100);
        transaction.GetColumn(names).Upsert(1, "Alice");
        transaction.Commit();
      }
      visits.Execute(transaction => transaction.Upsert(7, 1));
      cache.Upsert("greeting", "hello");
      await cluster.SyncWALAsync();
    }
    // No declarations: the catalog alone opens the cluster, and typed handles open the columns.
    using var reopened = Factory.OpenInProcess(Options());
    var domain = reopened.GetDomain("accounts");
    Assert.That(domain.ColumnNames, Is.EqualTo(new[] { "balances", "names" }));
    Assert.That(domain.GetColumn<long, long>("balances").TryGet(1, out var balance) && balance == 100, Is.True);
    Assert.That(domain.GetColumn<long, string>("names").TryGet(1, out var name) && name == "Alice", Is.True);
    using (var read = reopened.GetStandaloneColumn<int, int>("visits").BeginReadOnlyTransaction())
      Assert.That(read.TryGet(7, out var count) && count == 1, Is.True);
    Assert.That(reopened.GetRawColumn<string, string>("cache").TryGet("greeting", out var greeting) && greeting == "hello", Is.True);
    var schema = await reopened.Schema.DescribeAsync();
    Assert.That(schema.Columns.Select(column => column.Name), Is.EqualTo(new[] { "balances", "names", "visits", "cache" }));
    Assert.That(schema.Columns.Select(column => column.Kind), Is.EqualTo(new[]
    {
      ColumnKind.Coordinated, ColumnKind.Coordinated, ColumnKind.Standalone, ColumnKind.Raw
    }));
    Assert.That(schema.Columns[1].ValueSerializer, Is.EqualTo(new ColumnEncoding("zonetree.string.utf8", 1)));
    Assert.That(schema.Columns[0].Domain, Is.EqualTo("accounts"));
    Assert.That(schema.Columns[2].Domain, Is.Null);
    Assert.That(schema.SchemaEpoch, Is.EqualTo(5));
  }

  [Test]
  public async Task CreateRejectsExistingNamesAndGetOrCreateRejectsOtherDefinitions()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var domain = await cluster.Schema.CreateDomainAsync("accounts");
    Assert.ThrowsAsync<SchemaObjectAlreadyExistsException>(async () => await cluster.Schema.CreateDomainAsync("accounts"));
    Assert.That(await cluster.Schema.GetOrCreateDomainAsync("accounts"), Is.SameAs(domain));
    var balances = await cluster.Schema.CreateColumnAsync("accounts", new CoordinatedColumnDefinition<long, long>("balances"));
    Assert.ThrowsAsync<SchemaObjectAlreadyExistsException>(async () =>
        await cluster.Schema.CreateColumnAsync("accounts", new CoordinatedColumnDefinition<long, long>("balances")));
    Assert.That(await cluster.Schema.GetOrCreateColumnAsync("accounts", new CoordinatedColumnDefinition<long, long>("balances")),
        Is.SameAs(balances));
    Assert.ThrowsAsync<SchemaConflictException>(async () => await cluster.Schema.GetOrCreateColumnAsync(
        "accounts", new CoordinatedColumnDefinition<long, long>("balances") { ComparerId = "zonetree.int64.descending" }));
    Assert.ThrowsAsync<SchemaConflictException>(async () =>
        await cluster.Schema.GetOrCreateColumnAsync(new RawColumnDefinition<long, long>("balances")));
    Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        await cluster.Schema.CreateColumnAsync("missing", new CoordinatedColumnDefinition<long, long>("other")));
    Assert.ThrowsAsync<ColumnEncodingException>(async () => await cluster.Schema.CreateColumnAsync(
        new RawColumnDefinition<long, long>("unregistered") { ValueSerializerId = "missing.encoding" }));
    Assert.That((await cluster.Schema.DescribeAsync()).Columns.Select(column => column.Name), Is.EqualTo(new[] { "balances" }));
  }

  [Test]
  public async Task ConcurrentCreatorsOfOneColumnGetTheSameColumn()
  {
    using var cluster = Factory.CreateInProcess(Options());
    await cluster.Schema.CreateDomainAsync("accounts");
    var columns = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => cluster.Schema
        .GetOrCreateColumnAsync("accounts", new CoordinatedColumnDefinition<long, long>("balances")).AsTask()));
    Assert.That(columns.Distinct().Count(), Is.EqualTo(1));
    Assert.That((await cluster.Schema.DescribeAsync()).Columns, Has.Count.EqualTo(1));
  }

  [Test]
  public async Task DeclarationsAreCreatedWhenMissingInAnyOrderAndCanBeOmitted()
  {
    var first = new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      Domains = [new DomainDefinition("accounts") { Columns = [new CoordinatedColumnDefinition<long, long>("balances")] }],
      Columns = [new RawColumnDefinition<int, int>("a"), new RawColumnDefinition<int, int>("b")]
    };
    using (var cluster = Factory.CreateInProcess(first))
      cluster.GetRawColumn<int, int>("b").Upsert(1, 2);
    var reordered = new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      Columns = [new RawColumnDefinition<int, int>("c"), new RawColumnDefinition<int, int>("b")]
    };
    using (var cluster = Factory.OpenInProcess(reordered))
    {
      Assert.That(cluster.GetRawColumn<int, int>("b").TryGet(1, out var value) && value == 2, Is.True);
      Assert.That(cluster.Maintenance.ColumnNames, Is.EqualTo(new[] { "balances", "a", "b", "c" }));
      Assert.That(cluster.Maintenance.DomainNames, Is.EqualTo(new[] { "accounts" }));
    }
    using var empty = Factory.OpenInProcess(Options());
    Assert.That((await empty.Schema.DescribeAsync()).Columns, Has.Count.EqualTo(4));
  }

  [Test]
  public async Task AnEmptyClusterAndAnEmptyDomainCanBeCreated()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
      Assert.That((await cluster.Schema.DescribeAsync()).Domains, Is.Empty);
    Assert.Throws<DatabaseAlreadyExistsException>(() => Factory.CreateInProcess(Options()));
    using var reopened = Factory.OpenInProcess(new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      Domains = [new DomainDefinition("empty")]
    });
    Assert.That(reopened.GetDomain("empty").ColumnNames, Is.Empty);
  }

  [Test]
  public async Task CustomEncodingsAreResolvedByIdAndTheirVersionIsChecked()
  {
    var encodings = new ClusterEncodingRegistry().AddSerializer("test.reversed", 1, new ReversedStringSerializer());
    using (var cluster = Factory.CreateInProcess(Options(encodings)))
    {
      var cache = await cluster.Schema.CreateColumnAsync(
          new RawColumnDefinition<int, string>("cache") { ValueSerializerId = "test.reversed" });
      cache.Upsert(1, "abc");
      await cluster.SyncWALAsync();
    }
    using (var reopened = Factory.OpenInProcess(Options(
        new ClusterEncodingRegistry().AddSerializer("test.reversed", 1, new ReversedStringSerializer()))))
    {
      Assert.That(reopened.GetRawColumn<int, string>("cache").TryGet(1, out var value) && value == "abc", Is.True);
      // The registered encodings decide the types a handle can have.
      Assert.Throws<InvalidOperationException>(() => reopened.GetRawColumn<int, int>("cache"));
    }
    using (var changed = Factory.OpenInProcess(Options(
        new ClusterEncodingRegistry().AddSerializer("test.reversed", 2, new ReversedStringSerializer()))))
      Assert.Throws<ColumnEncodingException>(() => changed.GetRawColumn<int, string>("cache"));
    using var missing = Factory.OpenInProcess(Options());
    Assert.Throws<ColumnEncodingException>(() => missing.GetRawColumn<int, string>("cache"));
  }

  [Test]
  public void BuiltInEncodingsCannotBeReplacedAndTypesWithoutOneNeedAnId()
  {
    var encodings = new ClusterEncodingRegistry();
    Assert.Throws<ArgumentException>(() => encodings.AddSerializer("zonetree.int32", 1, new Int32Serializer()));
    Assert.Throws<ArgumentOutOfRangeException>(() => encodings.AddSerializer("test.zero", 0, new Int32Serializer()));
    var error = Assert.Throws<ColumnEncodingException>(() => Factory.CreateInProcess(new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      Columns = [new RawColumnDefinition<int, byte[]>("blobs")]
    }));
    Assert.That(error.Message, Does.Contain("blobs"));
    Assert.That(Directory.Exists(DataDirectory), Is.False);
  }

  [Test]
  public async Task AFailingStorageHookLeavesNoColumnInTheCatalog()
  {
    using var cluster = Factory.CreateInProcess(Options());
    Assert.ThrowsAsync<ArgumentException>(async () => await cluster.Schema.CreateColumnAsync(
        new RawColumnDefinition<int, int>("cache")
        {
          ConfigureStorage = storage => storage.SetValueSerializer(new Int32Serializer())
        }));
    Assert.That((await cluster.Schema.DescribeAsync()).Columns, Is.Empty);
    var cache = await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("cache"));
    cache.Upsert(1, 1);
  }

  [Test]
  public void AReadOnlyClusterRefusesSchemaChanges()
  {
    using var cluster = Factory.CreateInProcess(new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      IsReadOnly = true,
      Columns = [new RawColumnDefinition<int, int>("declared")]
    });
    Assert.ThrowsAsync<ZoneTreeIsReadOnlyException>(async () => await cluster.Schema.CreateDomainAsync("accounts"));
    Assert.ThrowsAsync<ZoneTreeIsReadOnlyException>(async () =>
        await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("other")));
    Assert.That(cluster.Maintenance.ColumnNames, Is.EqualTo(new[] { "declared" }));
  }

  [Test]
  public void DomainIdsAndTimestampsKeepIncreasingAcrossReopens()
  {
    long lastId = 0, lastCommit = 0;
    for (var round = 0; round < 3; ++round)
    {
      using var cluster = Factory.OpenOrCreateInProcess(new ZoneTreeClusterOptions
      {
        DataDirectory = DataDirectory,
        Logger = SilentTestLogger.Instance,
        Domains = [new DomainDefinition("accounts") { Columns = [new CoordinatedColumnDefinition<int, int>("balances")] }]
      });
      var domain = cluster.GetDomain("accounts");
      Assert.That(domain.LastCommitTimestamp, Is.GreaterThanOrEqualTo(lastCommit));
      var balances = domain.GetColumn<int, int>("balances");
      using var transaction = domain.BeginTransaction();
      Assert.That(transaction.TransactionId, Is.GreaterThan(lastId));
      transaction.GetColumn(balances).Upsert(round, round);
      transaction.Commit();
      Assert.That(transaction.CommitTimestamp, Is.GreaterThan(transaction.TransactionId));
      lastId = transaction.TransactionId;
      lastCommit = transaction.CommitTimestamp;
    }
  }

  [Test]
  public void TheTimestampSourceReservesAheadAndNeverPassesItsBound()
  {
    Factory.CreateInProcess(Options()).Dispose();
    using (var catalog = ClusterCatalog.Open(DataDirectory, SilentTestLogger.Instance, canCreate: false, canOpen: true))
    {
      var domain = catalog.CreateDomain("accounts");
      var source = new DomainTimestampSource(catalog, domain.Id, start: 0, bound: domain.TimestampBound);
      var previous = 0L;
      // Crossing several batches makes callers both run ahead of and wait for reservations.
      Parallel.For(0, 4, _ =>
      {
        for (var count = 0; count < (int)ClusterCatalog.TimestampBatch; ++count)
          source.Next();
      });
      for (var count = 0; count < 1000; ++count)
      {
        var value = source.Next();
        Assert.That(value, Is.GreaterThan(previous));
        previous = value;
      }
      Assert.That(previous, Is.EqualTo(4 * ClusterCatalog.TimestampBatch + 1000));
      Assert.That(catalog.State.FindDomain(domain.Id).TimestampBound, Is.GreaterThanOrEqualTo(previous));
    }
    using var reopened = ClusterCatalog.Open(DataDirectory, SilentTestLogger.Instance, canCreate: false, canOpen: true);
    Assert.That(reopened.State.FindDomain("accounts").TimestampBound,
        Is.GreaterThanOrEqualTo(4 * ClusterCatalog.TimestampBatch + 1000));
  }

  [Test]
  public void AColumnACrashLeftInCreationIsCreatedOnFirstUse()
  {
    Factory.CreateInProcess(Options()).Dispose();
    using (var catalog = ClusterCatalog.Open(DataDirectory, SilentTestLogger.Instance, canCreate: false, canOpen: true))
    {
      // The catalog record is durable, and the crash came before the column's storage existed.
      catalog.CreateColumn("cache", ColumnKind.Raw, 0,
          new ColumnEncoding("zonetree.int32", 1), new ColumnEncoding("zonetree.string.utf8", 1),
          new ColumnEncoding("zonetree.int32.ascending", 1), "System.Int32", "System.String");
    }
    // A directory that the catalog does not know is ignored.
    Directory.CreateDirectory(Path.Combine(DataDirectory, "columns", "999"));
    using (var cluster = Factory.OpenInProcess(Options()))
    {
      var cache = cluster.GetRawColumn<int, string>("cache");
      cache.Upsert(1, "one");
      Assert.That(cache.TryGet(1, out var value) && value == "one", Is.True);
    }
    using var reopened = ClusterCatalog.Open(DataDirectory, SilentTestLogger.Instance, canCreate: false, canOpen: true);
    Assert.That(reopened.State.FindColumn("cache").State, Is.EqualTo(ColumnState.Ready));
  }

  [Test]
  public async Task AReadyColumnWithoutItsStorageFailsInsteadOfOpeningEmpty()
  {
    // The first column of a cluster has id 1.
    const long id = 1;
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var cache = await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("cache"));
      cache.Upsert(1, 1);
    }
    Directory.Delete(Path.Combine(DataDirectory, "columns", id.ToString(System.Globalization.CultureInfo.InvariantCulture)), recursive: true);
    using var reopened = Factory.OpenInProcess(Options());
    Assert.Throws<DatabaseNotFoundException>(() => reopened.GetRawColumn<int, int>("cache"));
  }

  [Test]
  public async Task AMaintainerCoversColumnsThatOpenLater()
  {
    using var cluster = Factory.CreateInProcess(Options());
    using var maintainer = cluster.CreateMaintainer();
    maintainer.MaximumReadOnlySegmentCount = 7;
    var cache = await cluster.Schema.CreateColumnAsync(new RawColumnDefinition<int, int>("cache"));
    for (var key = 0; key < 100; ++key)
      cache.Upsert(key, key);
    maintainer.EvictToDisk();
    maintainer.WaitForBackgroundThreads();
    Assert.That(cluster.GetRawColumnMaintenance<int, int>("cache").InMemoryRecordCount, Is.Zero);
    Assert.That(maintainer.MaximumReadOnlySegmentCount, Is.EqualTo(7));
  }

  sealed class ReversedStringSerializer : ISerializer<string>
  {
    public string Deserialize(Memory<byte> bytes)
    {
      var value = System.Text.Encoding.UTF8.GetString(bytes.Span).ToCharArray();
      Array.Reverse(value);
      return new string(value);
    }

    public Memory<byte> Serialize(in string entry)
    {
      var value = entry.ToCharArray();
      Array.Reverse(value);
      return System.Text.Encoding.UTF8.GetBytes(value);
    }
  }
}
