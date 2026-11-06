using ZoneTree.AbstractFileStream;
using ZoneTree.Cluster;
using ZoneTree.Comparers;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.Cluster.UnitTests;

public sealed class ClusterHostTests
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
    DataDirectory = Path.Combine(root.FullName, ".tmp", "cluster-host", "tests", Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(DataDirectory))
      Directory.Delete(DataDirectory, recursive: true);
  }

  ZoneTreeClusterOptions Options() => new()
  {
    DataDirectory = DataDirectory,
    Logger = SilentTestLogger.Instance,
    Domains =
    [
      new DomainDefinition("accounts")
      {
        Columns =
        [
          new CoordinatedColumnDefinition<long, long>("balances")
          {
            ComparerId = "zonetree.int64.descending",
            KeySerializerId = "zonetree.int64"
          }
        ]
      }
    ],
    Columns =
    [
      new StandaloneColumnDefinition<int, int>("visits"),
      new RawColumnDefinition<int, string>("cache") { ValueSerializerId = "zonetree.string.utf8" }
    ]
  };

  [Test]
  public void TheHostGivesStorageAccessByColumnKind()
  {
    using var host = Factory.CreateInProcess(Options());
    var balances = host.GetCoordinatedColumnMaintenance<long, long>("balances");
    Assert.That(balances.ZoneTree.Maintenance.CloneOptions().MutableSegmentMaxItemCount, Is.GreaterThan(0));
    Assert.That(host.GetRawColumnMaintenance<int, string>("cache").CloneOptions().ValueSerializer,
        Is.InstanceOf<Utf8StringSerializer>());
    Assert.That(host.GetStandaloneColumnStorage<int, int>("visits").Maintenance.ActiveTransactionCount, Is.Zero);
    using (var maintainer = host.CreateColumnMaintainer("visits"))
      maintainer.EnableJobForCleaningInactiveCaches = false;

    Assert.Throws<SchemaConflictException>(() => host.GetRawColumnMaintenance<int, string>("visits"));
    Assert.Throws<SchemaConflictException>(() => host.GetStandaloneColumnStorage<long, long>("balances"));
    Assert.Throws<SchemaObjectNotFoundException>(() => host.GetCoordinatedColumnMaintenance<long, long>("missing"));
    Assert.Throws<SchemaObjectNotFoundException>(() => host.CreateColumnMaintainer("missing"));
  }

  [Test]
  public void TheColumnSchemaReachesTheStorage()
  {
    using var host = Factory.CreateInProcess(Options());
    var balances = host.GetDomain("accounts").GetColumn<long, long>("balances");
    using (var transaction = host.GetDomain("accounts").BeginTransaction())
    {
      transaction.GetColumn(balances).Upsert(1, 10);
      transaction.GetColumn(balances).Upsert(2, 20);
      transaction.Commit();
    }
    using var read = host.GetDomain("accounts").BeginReadOnlyTransaction();
    Assert.That(read.GetColumn(balances).Range(2, 1).Select(x => x.Key), Is.EqualTo(new long[] { 2, 1 }));
  }

  [Test]
  public void AStorageHookCannotReplaceTheColumnSchema()
  {
    var options = new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      Columns =
      [
        new StandaloneColumnDefinition<long, long>("visits")
        {
          ComparerId = "zonetree.int64.descending",
          ConfigureStorage = storage => storage.SetComparer(new Int64ComparerAscending())
        }
      ]
    };
    var error = Assert.Throws<ArgumentException>(() => Factory.CreateInProcess(options));
    Assert.That(error.Message, Does.Contain("visits"));
    // The failed creation left nothing behind, so the cluster can be created.
    Assert.That(Directory.Exists(DataDirectory), Is.False);
    using var created = Factory.CreateInProcess(new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      Columns = [new StandaloneColumnDefinition<long, long>("visits") { ComparerId = "zonetree.int64.descending" }]
    });
    Assert.That(created.Maintenance.ColumnNames, Is.EqualTo(new[] { "visits" }));
  }

  [Test]
  public void AStorageHookCannotChangeTheJournalOfACoordinatedColumn()
  {
    ZoneTreeClusterOptions Options(Action<ZoneTreeFactory<long, long>> configureStorage) => new()
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      Domains =
      [
        new DomainDefinition("accounts")
        {
          Columns = [new CoordinatedColumnDefinition<long, long>("balances") { ConfigureStorage = configureStorage }]
        }
      ]
    };
    var changes = new Action<ZoneTreeFactory<long, long>>[]
    {
      storage => storage.ConfigureWriteAheadLogOptions(wal => wal.WriteAheadLogMode = WriteAheadLogMode.AsyncShared),
      storage => storage.ConfigureWriteAheadLogOptions(wal => wal.CompressionMethod = CompressionMethod.LZ4),
      storage => storage.SetWriteAheadLogProvider(options => new WriteAheadLogProvider(options.Logger,
          new LocalFileStreamProvider(), Path.Combine(DataDirectory, "elsewhere")))
    };
    foreach (var change in changes)
    {
      var error = Assert.Throws<ArgumentException>(() => Factory.CreateInProcess(Options(change)));
      Assert.That(error.Message, Does.Contain("balances").And.Contain(nameof(ZoneTreeClusterOptions.CoordinatedWriteAheadLog)));
      Assert.That(Directory.Exists(DataDirectory), Is.False);
    }
    // A hook that keeps the journal's mode and format is fine.
    using var created = Factory.CreateInProcess(Options(storage => storage.SetMutableSegmentMaxItemCount(1_000)
        .ConfigureWriteAheadLogOptions(wal => wal.CompressionLevel = 5)));
    Assert.That(created.Maintenance.ColumnNames, Is.EqualTo(new[] { "balances" }));
  }

  [Test]
  public async Task TheClientContractCoversRemoteUseCases()
  {
    IZoneTreeCluster client = Factory.CreateInProcess(Options());
    try
    {
      var accounts = client.GetDomain("accounts");
      Assert.That(await accounts.GetTransactionOutcomeAsync(1), Is.EqualTo(TransactionOutcome.Unknown));
      await client.SyncWALAsync();
    }
    finally { await client.DisposeAsync(); }
    Assert.ThrowsAsync<NotSupportedException>(async () => await Factory.ConnectAsync(
        new ZoneTreeClusterConnectionOptions { Endpoints = [new Uri("tcp://localhost:5000")] }));
  }
}
