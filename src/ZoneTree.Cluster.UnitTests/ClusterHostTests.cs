using ZoneTree.Cluster;
using ZoneTree.Comparers;
using ZoneTree.Serializers;

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

    Assert.Throws<InvalidOperationException>(() => host.GetRawColumnMaintenance<int, string>("visits"));
    Assert.Throws<InvalidOperationException>(() => host.GetStandaloneColumnStorage<long, long>("balances"));
    Assert.Throws<KeyNotFoundException>(() => host.GetCoordinatedColumnMaintenance<long, long>("missing"));
    Assert.Throws<KeyNotFoundException>(() => host.CreateColumnMaintainer("missing"));
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
