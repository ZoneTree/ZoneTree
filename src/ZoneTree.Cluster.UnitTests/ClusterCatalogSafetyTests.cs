using ZoneTree.Cluster;
using ZoneTree.Logger;

namespace ZoneTree.Cluster.UnitTests;

/// <summary>
/// A catalog that does not match the cluster's storage, after storage damage or an incomplete
/// restore, refuses to open instead of starting over over existing columns.
/// </summary>
public sealed class ClusterCatalogSafetyTests
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
    DataDirectory = Path.Combine(root.FullName, ".tmp", "cluster-catalog-safety", "tests", Guid.NewGuid().ToString("N"));
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
      new DomainDefinition("accounts")
      {
        Columns = (columns.Length == 0 ? ["balances"] : columns)
            .Select(name => (CoordinatedColumnDefinition)new CoordinatedColumnDefinition<long, long>(name))
            .ToArray()
      }
    ]
  };

  string CatalogDirectory => Path.Combine(DataDirectory, "catalog");

  string ColumnsDirectory => Path.Combine(DataDirectory, "columns");

  void CreateClusterWithData()
  {
    using var cluster = Factory.CreateInProcess(Options());
    cluster.GetDomain("accounts").GetColumn<long, long>("balances").Upsert(1, 100);
  }

  [Test]
  public void ACatalogThatLostItsClusterRecordIsRefused()
  {
    CreateClusterWithData();
    // Storage damage removes the cluster record, and keeps the domain and column records.
    using (var catalog = new ZoneTreeFactory<string, Memory<byte>>()
        .SetDataDirectory(CatalogDirectory)
        .SetLogger(SilentTestLogger.Instance)
        .OpenOrCreateTransactional())
    {
      using var transaction = catalog.BeginTransaction();
      transaction.Delete(CatalogRecords.ClusterKey);
      transaction.Commit();
    }
    Assert.Throws<ClusterCatalogCorruptionException>(() => Factory.OpenOrCreateInProcess(Options()));
    Assert.Throws<ClusterCatalogCorruptionException>(() => Factory.OpenInProcess(Options()));
    Assert.That(Directory.EnumerateFileSystemEntries(ColumnsDirectory), Is.Not.Empty);
  }

  [Test]
  public void ColumnStorageWithoutACatalogIsRefused()
  {
    CreateClusterWithData();
    // An incomplete restore brings back the columns but not the catalog.
    Directory.Delete(CatalogDirectory, recursive: true);
    Assert.Throws<ClusterCatalogCorruptionException>(() => Factory.OpenOrCreateInProcess(Options()));
    Assert.That(Directory.Exists(CatalogDirectory), Is.False);
    Assert.That(Directory.EnumerateFileSystemEntries(ColumnsDirectory), Is.Not.Empty);
  }

  [Test]
  public void ACatalogWithoutColumnsIsCreatedAndOpened()
  {
    // A crash right after the catalog was created leaves no cluster record and nothing else.
    using (new ZoneTreeFactory<string, Memory<byte>>()
        .SetDataDirectory(CatalogDirectory)
        .SetLogger(SilentTestLogger.Instance)
        .OpenOrCreateTransactional())
    {
    }
    using (var cluster = Factory.OpenOrCreateInProcess(Options()))
      cluster.GetDomain("accounts").GetColumn<long, long>("balances").Upsert(1, 100);
    using var reopened = Factory.OpenInProcess(Options());
    Assert.That(reopened.GetDomain("accounts").GetColumn<long, long>("balances").ContainsKey(1), Is.True);
  }

  [Test]
  public void CreatingAColumnOverExistingStorageIsRefused()
  {
    CreateClusterWithData();
    long nextId;
    using (var cluster = (ZoneTreeCluster)Factory.OpenInProcess(Options()))
      nextId = cluster.Catalog.State.Cluster.NextColumnId;
    // Storage of a column the catalog lost sits under the id the next column takes.
    var lost = Path.Combine(ColumnsDirectory, nextId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Directory.CreateDirectory(lost);
    File.WriteAllText(Path.Combine(lost, "data"), "lost");

    Assert.Throws<ClusterCatalogCorruptionException>(() => Factory.OpenInProcess(Options("balances", "names")));
    Assert.That(File.Exists(Path.Combine(lost, "data")), Is.True);
    using var reopened = Factory.OpenInProcess(Options());
    Assert.That(reopened.Maintenance.ColumnNames, Is.EqualTo(new[] { "balances" }));
    Assert.That(reopened.GetDomain("accounts").GetColumn<long, long>("balances").ContainsKey(1), Is.True);
  }
}
