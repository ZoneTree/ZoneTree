using ZoneTree.Cluster;

namespace ZoneTree.UnitTests;

public sealed class ClusterDomainTests
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
    DataDirectory = Path.Combine(root.FullName, ".tmp", "cluster-domains", "tests", Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(DataDirectory))
      Directory.Delete(DataDirectory, recursive: true);
  }

  ZoneTreeClusterOptions Options(params DomainDefinition[] domains) => new()
  {
    DataDirectory = DataDirectory,
    Logger = SilentTestLogger.Instance,
    Domains = domains.Length != 0 ? domains :
    [
      new DomainDefinition("accounts")
      {
        Columns =
        [
          new CoordinatedColumnDefinition<long, long>("balances"),
          new CoordinatedColumnDefinition<long, string>("audit")
        ]
      },
      new DomainDefinition("catalog") { Columns = [new CoordinatedColumnDefinition<long, string>("products")] }
    ]
  };

  [Test]
  public void TransactionsSpanTheColumnsOfOneDomain()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var accounts = cluster.GetDomain("accounts");
    var catalog = cluster.GetDomain("catalog");
    var balances = accounts.GetColumn<long, long>("balances");
    var audit = accounts.GetColumn<long, string>("audit");
    var products = catalog.GetColumn<long, string>("products");
    Assert.That(balances.Domain, Is.SameAs(accounts));
    Assert.That(accounts.ColumnNames, Is.EqualTo(new[] { "balances", "audit" }));
    Assert.That(cluster.Maintenance.DomainNames, Is.EqualTo(new[] { "accounts", "catalog" }));

    using (var transaction = accounts.BeginTransaction())
    {
      transaction.GetColumn(balances).Upsert(1, 100);
      transaction.GetColumn(audit).Upsert(1, "opened");
      Assert.Throws<ArgumentException>(() => transaction.GetColumn(products));
      transaction.Commit();
    }
    Assert.That(accounts.LastCommitTimestamp, Is.GreaterThan(0));
    Assert.That(catalog.LastCommitTimestamp, Is.Zero);
    Assert.That(balances.TryGet(1, out var balance) && balance == 100, Is.True);
    Assert.That(audit.TryGet(1, out var entry) && entry == "opened", Is.True);

    Assert.Throws<KeyNotFoundException>(() => cluster.GetDomain("missing"));
    Assert.Throws<KeyNotFoundException>(() => catalog.GetColumn<long, long>("balances"));
    Assert.Throws<InvalidOperationException>(() => accounts.GetColumn<long, string>("balances"));
  }

  [Test]
  public void InvalidDomainDeclarationsAreRejected()
  {
    Assert.Throws<ArgumentException>(() => Factory.CreateInProcess(new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      Columns = [new CoordinatedColumnDefinition<long, long>("balances")]
    }));
    Assert.Throws<ArgumentException>(() => Factory.CreateInProcess(Options(new DomainDefinition("empty"))));
    Assert.Throws<ArgumentException>(() => Factory.CreateInProcess(Options(
        new DomainDefinition("same") { Columns = [new CoordinatedColumnDefinition<long, long>("a")] },
        new DomainDefinition("same") { Columns = [new CoordinatedColumnDefinition<long, long>("b")] })));
    Assert.Throws<ArgumentException>(() => Factory.CreateInProcess(Options(
        new DomainDefinition("first") { Columns = [new CoordinatedColumnDefinition<long, long>("a")] },
        new DomainDefinition("second") { Columns = [new CoordinatedColumnDefinition<long, long>("a")] })));
    Assert.That(Directory.Exists(DataDirectory), Is.False);
  }

  [Test]
  public void ReopeningWithAColumnInAnotherDomainFails()
  {
    DomainDefinition[] Domains(string domainOfB) =>
    [
      new DomainDefinition("first")
      {
        Columns = domainOfB == "first"
            ? [new CoordinatedColumnDefinition<long, long>("a"), new CoordinatedColumnDefinition<long, long>("b")]
            : [new CoordinatedColumnDefinition<long, long>("a")]
      },
      new DomainDefinition("second")
      {
        Columns = domainOfB == "second"
            ? [new CoordinatedColumnDefinition<long, long>("b"), new CoordinatedColumnDefinition<long, long>("c")]
            : [new CoordinatedColumnDefinition<long, long>("c")]
      }
    ];
    using (Factory.CreateInProcess(Options(Domains("first"))))
    {
    }
    Assert.Throws<InvalidDataException>(() => Factory.OpenInProcess(Options(Domains("second"))));
    using var reopened = Factory.OpenInProcess(Options(Domains("first")));
    Assert.That(reopened.GetDomain("first").ColumnNames, Is.EqualTo(new[] { "a", "b" }));
  }
}
