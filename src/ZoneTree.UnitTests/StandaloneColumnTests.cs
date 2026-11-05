using ZoneTree.Exceptions;
using ZoneTree.Cluster;

namespace ZoneTree.UnitTests;

public sealed class StandaloneColumnTests
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
    DataDirectory = Path.Combine(root.FullName, ".tmp", "cluster-standalone", "tests", Guid.NewGuid().ToString("N"));
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
    Columns = columns.Length != 0 ? columns :
    [
      new CoordinatedColumnDefinition<int, int>("balances"),
      new StandaloneColumnDefinition<int, int>("visits")
    ]
  };

  [Test]
  public void StandaloneTransactionsCommitAndSurviveAReopen()
  {
    using (var cluster = Factory.CreateInProcess(Options()))
    {
      var visits = cluster.GetStandaloneColumn<int, int>("visits");
      using (var transaction = visits.BeginTransaction())
      {
        transaction.Upsert(1, 10);
        transaction.Commit();
      }
      visits.Execute(transaction =>
      {
        Assert.That(transaction.TryGet(1, out var count), Is.True);
        transaction.Upsert(1, count + 1);
      });
      using var read = visits.BeginReadOnlyTransaction();
      Assert.That(read.TryGet(1, out var value) && value == 11, Is.True);
    }
    using var reopened = Factory.OpenInProcess(Options());
    using var snapshot = reopened.GetStandaloneColumn<int, int>("visits").BeginReadOnlyTransaction();
    Assert.That(snapshot.TryGet(1, out var persisted) && persisted == 11, Is.True);
  }

  [Test]
  public void HandlesAreTypedByColumnKind()
  {
    using var cluster = Factory.CreateInProcess(Options());
    Assert.Throws<InvalidOperationException>(() => cluster.GetCoordinatedColumn<int, int>("visits"));
    Assert.Throws<InvalidOperationException>(() => cluster.GetRawColumn<int, int>("visits"));
    Assert.Throws<InvalidOperationException>(() => cluster.GetStandaloneColumn<int, int>("balances"));
    Assert.Throws<InvalidOperationException>(() => cluster.GetStandaloneColumn<long, int>("visits"));
    Assert.That(cluster.GetStandaloneColumn<int, int>("visits").Name, Is.EqualTo("visits"));
  }

  [TestCase(true)]
  [TestCase(false)]
  public void ReopeningWithAnotherKindFails(bool standalone)
  {
    ColumnDefinition Definition(bool isStandalone) => isStandalone
        ? new StandaloneColumnDefinition<int, int>("visits")
        : new CoordinatedColumnDefinition<int, int>("visits");
    using (Factory.CreateInProcess(Options(Definition(standalone))))
    {
    }
    Assert.Throws<InvalidDataException>(() => Factory.OpenInProcess(Options(Definition(!standalone))));
    using var reopened = Factory.OpenInProcess(Options(Definition(standalone)));
  }

  [Test]
  public void DisposingAHandleDoesNotCloseTheColumn()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var visits = cluster.GetStandaloneColumn<int, int>("visits");
    using (visits)
    {
    }
    visits.Execute(transaction => transaction.Upsert(1, 1));
    using var read = cluster.GetStandaloneColumn<int, int>("visits").BeginReadOnlyTransaction();
    Assert.That(read.ContainsKey(1), Is.True);
  }

  [Test]
  public async Task ClusterSettingsAndSyncsIncludeStandaloneColumns()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var visits = cluster.GetStandaloneColumn<int, int>("visits");
    visits.Execute(transaction => transaction.Upsert(1, 1));
    await cluster.SyncWALAsync();
    cluster.IsReadOnly = true;
    Assert.That(visits.IsReadOnly, Is.True);
    Assert.Throws<ZoneTreeIsReadOnlyException>(() => visits.BeginTransaction());
    cluster.IsReadOnly = false;
    visits.Execute(transaction => transaction.Upsert(2, 2));
    Assert.That(cluster.Maintenance.AbortedTransactionCount, Is.Zero);
  }
}
