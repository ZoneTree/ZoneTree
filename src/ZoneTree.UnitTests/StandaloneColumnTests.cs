using ZoneTree.Exceptions;
using ZoneTree.Cluster;
using ZoneTree.Transactional;

namespace ZoneTree.UnitTests;

public sealed class StandaloneColumnTests
{
  string DataDirectory;
  readonly ZoneTreeClusterFactory Factory = new();
  const string Domain = ClusterTestDomains.Name;

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

  ZoneTreeClusterOptions Options(params ColumnDefinition[] columns)
  {
    if (columns.Length == 0)
      columns = [new CoordinatedColumnDefinition<int, int>("balances"), new StandaloneColumnDefinition<int, int>("visits")];
    return new()
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      Domains = ClusterTestDomains.Domains(columns),
      Columns = ClusterTestDomains.Others(columns)
    };
  }

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
    Assert.Throws<KeyNotFoundException>(() => cluster.GetDomain(Domain).GetColumn<int, int>("visits"));
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
  public async Task ClusterSettingsAndSyncsIncludeStandaloneColumns()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var visits = cluster.GetStandaloneColumn<int, int>("visits");
    visits.Execute(transaction => transaction.Upsert(1, 1));
    await cluster.SyncWALAsync();
    cluster.IsReadOnly = true;
    Assert.That(cluster.GetStandaloneColumnStorage<int, int>("visits").IsReadOnly, Is.True);
    Assert.Throws<ZoneTreeIsReadOnlyException>(() => visits.BeginTransaction());
    cluster.IsReadOnly = false;
    visits.Execute(transaction => transaction.Upsert(2, 2));
    Assert.That(cluster.Maintenance.AbortedTransactionCount, Is.Zero);
  }

  [Test]
  public async Task StandaloneTransactionsHaveTheClientTransactionApi()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var visits = cluster.GetStandaloneColumn<int, int>("visits");
    await using (var transaction = await visits.BeginTransactionAsync())
    {
      // The async operations of a transaction run in parallel.
      var writes = Enumerable.Range(100, 50).Select(i => transaction.UpsertAsync(i, i).AsTask()).ToArray();
      await Task.WhenAll(writes);
      await transaction.UpsertAsync(1, 50);
      var own = await transaction.TryGetAsync(1);
      Assert.That(own.Found && own.Value == 50, Is.True);
      await transaction.CommitAsync();
      Assert.That(transaction.State, Is.EqualTo(TransactionState.Committed));
    }
    await visits.ExecuteDurableAsync(async (transaction, token) =>
    {
      var count = await transaction.TryGetForUpdateAsync(1, token);
      await transaction.UpsertAsync(1, count.Value + 1, token);
    });
    using (var rolledBack = visits.BeginTransaction())
    {
      rolledBack.Upsert(2, 2);
      await rolledBack.RollbackAsync();
      Assert.That(rolledBack.State, Is.EqualTo(TransactionState.Aborted));
    }
    await using (var read = await visits.BeginReadOnlyTransactionAsync())
    {
      Assert.That(read.TryGet(1, out var value) && value == 51, Is.True);
      Assert.That(await read.ContainsKeyAsync(2), Is.False);
      Assert.That(read.Range(0, 10).Select(x => x.Key), Is.EqualTo(new[] { 1 }));
      Assert.That(read.Range(100, 200).Count(), Is.EqualTo(50));
    }
    Assert.That(await visits.GetTransactionOutcomeAsync(1), Is.EqualTo(TransactionOutcome.Unknown));
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  [Test]
  public void DisposingTheClusterRollsBackOpenStandaloneTransactions()
  {
    var cluster = Factory.CreateInProcess(Options());
    var visits = cluster.GetStandaloneColumn<int, int>("visits");
    visits.Execute(transaction => transaction.Upsert(1, 1));
    var writer = visits.BeginTransaction();
    writer.Upsert(1, 42);
    var reader = visits.BeginReadOnlyTransaction();
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.EqualTo(2));
    cluster.Dispose();
    Assert.That(writer.State, Is.EqualTo(TransactionState.Aborted));
    writer.Dispose();
    reader.Dispose();

    using var reopened = Factory.OpenInProcess(Options());
    using var read = reopened.GetStandaloneColumn<int, int>("visits").BeginReadOnlyTransaction();
    Assert.That(read.TryGet(1, out var value) && value == 1, Is.True);
  }
}
