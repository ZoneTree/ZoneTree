using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.Cluster;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class RawColumnTests
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
    DataDirectory = Path.Combine(root.FullName, ".tmp", "cluster-raw", "tests", Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(DataDirectory))
      Directory.Delete(DataDirectory, recursive: true);
  }

  const string Domain = ClusterTestDomains.Name;

  ZoneTreeClusterOptions Options(params ColumnDefinition[] columns)
  {
    if (columns.Length == 0)
      columns = [new CoordinatedColumnDefinition<int, int>("balances"), new RawColumnDefinition<int, string>("cache")];
    return new()
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      Domains = ClusterTestDomains.Domains(columns),
      Columns = ClusterTestDomains.Others(columns)
    };
  }

  [Test]
  public void StorageDefaultsApplyToEveryColumnWithIndependentOverridesAndReopen()
  {
    var defaults = new TestStorageDefaults();
    var options = new ZoneTreeClusterOptions
    {
      DataDirectory = DataDirectory,
      Logger = SilentTestLogger.Instance,
      StorageDefaults = defaults,
      Domains =
      [
        new DomainDefinition(Domain)
        {
          Columns =
          [
            new CoordinatedColumnDefinition<int, int>("balances")
            {
              ConfigureStorage = storage => storage.SetMutableSegmentMaxItemCount(20_000)
                  .ConfigureWriteAheadLogOptions(wal => wal.WriteAheadLogMode = WriteAheadLogMode.Shared)
            },
            new CoordinatedColumnDefinition<long, string>("names")
          ]
        }
      ],
      Columns =
      [
        new RawColumnDefinition<int, string>("cache")
        {
          ConfigureStorage = storage => storage.ConfigureDiskSegmentOptions(disk => disk.KeyCacheSize = 256)
        },
        new RawColumnDefinition<long, long>("counters")
      ]
    };

    static void Verify(IZoneTreeCluster cluster)
    {
      var balances = cluster.GetDomain(Domain).GetColumn<int, int>("balances").Maintenance.ZoneTree.Maintenance.CloneOptions();
      var names = cluster.GetDomain(Domain).GetColumn<long, string>("names").Maintenance.ZoneTree.Maintenance.CloneOptions();
      var cache = cluster.GetRawColumn<int, string>("cache").Maintenance.CloneOptions();
      var counters = cluster.GetRawColumn<long, long>("counters").Maintenance.CloneOptions();
      Assert.That(balances.MutableSegmentMaxItemCount, Is.EqualTo(20_000));
      Assert.That(balances.WriteAheadLogOptions.WriteAheadLogMode, Is.EqualTo(WriteAheadLogMode.Shared));
      Assert.That(names.MutableSegmentMaxItemCount, Is.EqualTo(10_000));
      Assert.That(names.WriteAheadLogOptions.WriteAheadLogMode, Is.EqualTo(WriteAheadLogMode.AsyncShared));
      Assert.That(cache.MutableSegmentMaxItemCount, Is.EqualTo(10_000));
      Assert.That(cache.WriteAheadLogOptions.WriteAheadLogMode, Is.EqualTo(WriteAheadLogMode.AsyncShared));
      Assert.That(cache.DiskSegmentOptions.KeyCacheSize, Is.EqualTo(256));
      Assert.That(counters.MutableSegmentMaxItemCount, Is.EqualTo(10_000));
      Assert.That(counters.DiskSegmentOptions.KeyCacheSize, Is.EqualTo(128));
      Assert.That(names.DiskSegmentOptions.KeyCacheSize, Is.EqualTo(128));
    }

    using (var cluster = Factory.CreateInProcess(options))
    {
      Verify(cluster);
      cluster.GetDomain(Domain).GetColumn<int, int>("balances").Upsert(1, 100);
      cluster.GetDomain(Domain).GetColumn<long, string>("names").Upsert(1, "Alice");
      cluster.GetRawColumn<int, string>("cache").Upsert(1, "cached");
      cluster.GetRawColumn<long, long>("counters").Upsert(1, 7);
    }

    using var reopened = Factory.OpenInProcess(options);
    Verify(reopened);
    Assert.That(reopened.GetDomain(Domain).GetColumn<int, int>("balances").TryGet(1, out var balance), Is.True);
    Assert.That(balance, Is.EqualTo(100));
    Assert.That(reopened.GetDomain(Domain).GetColumn<long, string>("names").TryGet(1, out var name), Is.True);
    Assert.That(name, Is.EqualTo("Alice"));
    Assert.That(reopened.GetRawColumn<int, string>("cache").TryGet(1, out var cached), Is.True);
    Assert.That(cached, Is.EqualTo("cached"));
    Assert.That(reopened.GetRawColumn<long, long>("counters").TryGet(1, out var counter), Is.True);
    Assert.That(counter, Is.EqualTo(7));
    Assert.That(defaults.Columns, Is.EqualTo(new[]
    {
      ("balances", typeof(int), typeof(int)), ("names", typeof(long), typeof(string)),
      ("cache", typeof(int), typeof(string)), ("counters", typeof(long), typeof(long)),
      ("balances", typeof(int), typeof(int)), ("names", typeof(long), typeof(string)),
      ("cache", typeof(int), typeof(string)), ("counters", typeof(long), typeof(long))
    }));
  }

  sealed class TestStorageDefaults : IColumnStorageConfigurator
  {
    public List<(string Name, Type KeyType, Type ValueType)> Columns { get; } = [];

    public void Configure<TKey, TValue>(ColumnDefinition column, ZoneTreeFactory<TKey, TValue> storage)
    {
      Columns.Add((column.Name, typeof(TKey), typeof(TValue)));
      storage.SetMutableSegmentMaxItemCount(10_000)
          .ConfigureWriteAheadLogOptions(wal => wal.WriteAheadLogMode = WriteAheadLogMode.AsyncShared)
          .ConfigureDiskSegmentOptions(disk => disk.KeyCacheSize = 128);
    }
  }

  [Test]
  public void RawWritesAreImmediateAndIndependentOfTransactionRollback()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var balances = cluster.GetDomain(Domain).GetColumn<int, int>("balances");
    var cache = cluster.GetRawColumn<int, string>("cache");
    Assert.That(cache.Cluster, Is.SameAs(cluster));
    Assert.That(cache, Is.Not.InstanceOf<ICoordinatedColumn<int, string>>());
    using (var transaction = cluster.GetDomain(Domain).BeginTransaction())
    {
      transaction.GetColumn(balances).Upsert(1, 100);
      cache.Upsert(1, "first");
      Assert.That(cache.TryGet(1, out var current), Is.True);
      Assert.That(current, Is.EqualTo("first"));
      transaction.Rollback();
    }
    Assert.That(balances.ContainsKey(1), Is.False);
    Assert.That(cache.ContainsKey(1), Is.True);
    cache.Upsert(1, "second");
    Assert.That(cache.TryGet(1, out var updated), Is.True);
    Assert.That(updated, Is.EqualTo("second"));
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
    Assert.That(cluster.GetDomain(Domain).LastCommitTimestamp, Is.Zero);
    Assert.That(cluster.Maintenance.ConflictIndexCount, Is.Zero);
    Assert.Throws<KeyNotFoundException>(() => cluster.GetDomain(Domain).GetColumn<int, string>("cache"));
    Assert.Throws<InvalidOperationException>(() => cluster.GetRawColumn<int, int>("balances"));
  }

  [Test]
  public async Task RawAsyncOperationsAndRangesPreserveDirectSemantics()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var cache = cluster.GetRawColumn<int, string>("cache");
    for (var key = 1; key <= 5; ++key)
      await cache.UpsertAsync(key, key.ToString());
    await cache.DeleteAsync(3);
    Assert.That((await cache.TryGetAsync(3)).Found, Is.False);
    Assert.That(await cache.ContainsKeyAsync(2), Is.True);
    var found = await cache.TryGetAsync(2);
    Assert.That(found.Value, Is.EqualTo("2"));
    Assert.That(cache.Range(2, 5).Select(record => record.Key), Is.EqualTo(new[] { 2, 4, 5 }));
    Assert.That(cache.Range(2, 5, reverse: true).Select(record => record.Key), Is.EqualTo(new[] { 5, 4, 2 }));
    Assert.That(cache.Range(5, 2), Is.Empty);
    var keys = new List<int>();
    await foreach (var record in cache.RangeAsync(2, 5)) keys.Add(record.Key);
    Assert.That(keys, Is.EqualTo(new[] { 2, 4, 5 }));
    await using var iterator = cache.CreateAsyncReverseIterator();
    await iterator.SeekAsync(4);
    keys.Clear();
    while (await iterator.NextAsync()) keys.Add(iterator.CurrentKey);
    Assert.That(keys, Is.EqualTo(new[] { 4, 2, 1 }));
    await cache.SyncWALAsync();
  }

  [Test]
  public async Task CancelledRawWritesHaveNoEffectAndAsyncRangesObserveBothTokens()
  {
    using var cluster = Factory.CreateInProcess(Options());
    var cache = cluster.GetRawColumn<int, string>("cache");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Assert.ThrowsAsync<OperationCanceledException>(async () => await cache.UpsertAsync(1, "cancelled", cancellation.Token));
    Assert.That(cache.ContainsKey(1), Is.False);
    cache.Upsert(1, "kept");
    Assert.ThrowsAsync<OperationCanceledException>(async () => await cache.DeleteAsync(1, cancellation.Token));
    Assert.That(cache.ContainsKey(1), Is.True);
    await using var rangeToken = cache.RangeAsync(1, 5, cancellationToken: cancellation.Token).GetAsyncEnumerator();
    await using var enumerationToken = cache.RangeAsync(1, 5).GetAsyncEnumerator(cancellation.Token);
    Assert.ThrowsAsync<OperationCanceledException>(async () => await rangeToken.MoveNextAsync());
    Assert.ThrowsAsync<OperationCanceledException>(async () => await enumerationToken.MoveNextAsync());
  }

  [Test]
  public void DatabaseReadOnlyBlocksRawWritesAndNewTransactions()
  {
    var cluster = Factory.CreateInProcess(Options());
    var cache = cluster.GetRawColumn<int, string>("cache");
    try
    {
      cache.Upsert(1, "value");
      using var existing = cluster.GetDomain(Domain).BeginTransaction();
      cluster.IsReadOnly = true;
      Assert.Throws<ZoneTreeIsReadOnlyException>(() => cache.Upsert(2, "blocked"));
      Assert.Throws<ZoneTreeIsReadOnlyException>(() => cache.Delete(1));
      Assert.Throws<ZoneTreeIsReadOnlyException>(() => cluster.GetDomain(Domain).BeginTransaction());
      Assert.That(cache.ContainsKey(1), Is.True);
      existing.GetColumn(cluster.GetDomain(Domain).GetColumn<int, int>("balances")).Upsert(1, 100);
      existing.Commit();
      cluster.IsReadOnly = false;
      cache.Delete(1);
      Assert.That(cache.ContainsKey(1), Is.False);
    }
    finally { cluster.Dispose(); }
    Assert.Throws<ObjectDisposedException>(() => cache.TryGet(1, out _));
    Assert.Throws<ObjectDisposedException>(() => cache.Upsert(1, "closed"));
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task MixedColumnsCanSyncAndReopen(WriteAheadLogMode mode)
  {
    var options = Options(new CoordinatedColumnDefinition<int, int>("balances"),
        new RawColumnDefinition<int, string>("cache")
        {
          ConfigureStorage = factory => factory.ConfigureWriteAheadLogOptions(wal => wal.WriteAheadLogMode = mode)
        });
    using (var cluster = Factory.CreateInProcess(options))
    {
      cluster.GetDomain(Domain).GetColumn<int, int>("balances").Upsert(1, 100);
      cluster.GetRawColumn<int, string>("cache").Upsert(1, "value");
      await cluster.SyncWALAsync();
    }
    using var reopened = Factory.OpenInProcess(options);
    Assert.That(reopened.GetDomain(Domain).GetColumn<int, int>("balances").TryGet(1, out var balance), Is.True);
    Assert.That(balance, Is.EqualTo(100));
    Assert.That(reopened.GetRawColumn<int, string>("cache").TryGet(1, out var value), Is.True);
    Assert.That(value, Is.EqualTo("value"));
    Assert.That(reopened.Maintenance.ColumnNames, Is.EqualTo(new[] { "balances", "cache" }));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void ReopeningWithADifferentColumnKindFailsBeforeOpeningItsTree(bool originallyRaw)
  {
    ColumnDefinition Definition(bool raw) => raw
        ? new RawColumnDefinition<int, string>("data")
        : new CoordinatedColumnDefinition<int, string>("data");
    var original = Options(Definition(originallyRaw));
    using (Factory.CreateInProcess(original)) { }
    Assert.Throws<InvalidDataException>(() => Factory.OpenOrCreateInProcess(Options(Definition(!originallyRaw))));
    using var reopened = Factory.OpenInProcess(original);
    Assert.That(reopened.Maintenance.ColumnNames, Is.EqualTo(new[] { "data" }));
  }

  [Test]
  public async Task DatabaseSyncIncludesBothKindsAndAutomaticSyncIncludesRawWrites()
  {
    var transactionalFiles = new PowerLossFileStreamProvider();
    var rawFiles = new PowerLossFileStreamProvider();
    void Configure<TKey, TValue>(ZoneTreeFactory<TKey, TValue> factory, PowerLossFileStreamProvider files, string name)
    {
      factory.ConfigureWriteAheadLogOptions(wal => wal.SyncCompressedModeOptions.EnableTailWriterJob = false)
          .SetWriteAheadLogProvider(options => new WriteAheadLogProvider(
              options.Logger, files, Path.Combine(DataDirectory, name)));
    }
    using var cluster = Factory.CreateInProcess(Options(
        new CoordinatedColumnDefinition<int, int>("balances")
        {
          ConfigureStorage = factory => Configure(factory, transactionalFiles, "tx-wal")
        },
        new RawColumnDefinition<int, string>("cache")
        {
          ConfigureStorage = factory => Configure(factory, rawFiles, "raw-wal")
        }));
    var cache = cluster.GetRawColumn<int, string>("cache");
    cluster.GetDomain(Domain).GetColumn<int, int>("balances").Upsert(1, 100);
    cache.Upsert(1, "first");
    var beforeTransactional = transactionalFiles.DiskSyncCount;
    var beforeRaw = rawFiles.DiskSyncCount;
    await cluster.SyncWALAsync();
    Assert.That(transactionalFiles.DiskSyncCount, Is.GreaterThan(beforeTransactional));
    Assert.That(rawFiles.DiskSyncCount, Is.GreaterThan(beforeRaw));
    cache.Upsert(1, "second");
    beforeRaw = rawFiles.DiskSyncCount;
    cluster.AutoSyncInterval = TimeSpan.FromMilliseconds(5);
    Assert.That(SpinWait.SpinUntil(() => rawFiles.DiskSyncCount > beforeRaw, TimeSpan.FromSeconds(5)), Is.True);
    cluster.AutoSyncInterval = TimeSpan.Zero;
  }

  [Test]
  public async Task RawByteArraysCanUseAnExplicitSerializerAndReopen()
  {
    var options = Options(new RawColumnDefinition<string, byte[]>("cache")
    {
      ConfigureStorage = factory => factory.SetValueSerializer(new BytesSerializer())
    });
    using (var cluster = Factory.CreateInProcess(options))
    {
      var cache = cluster.GetRawColumn<string, byte[]>("cache");
      cache.Upsert("keep", [1, 2, 3]);
      cache.Upsert("remove", [9]);
      cache.Delete("remove");
      await cluster.SyncWALAsync();
    }
    using var reopened = Factory.OpenInProcess(options);
    var restored = reopened.GetRawColumn<string, byte[]>("cache");
    Assert.That(restored.TryGet("keep", out var value), Is.True);
    Assert.That(value, Is.EqualTo(new byte[] { 1, 2, 3 }));
    Assert.That(restored.ContainsKey("remove"), Is.False);
  }

  sealed class BytesSerializer : ISerializer<byte[]>
  {
    public byte[] Deserialize(Memory<byte> bytes) => bytes.IsEmpty ? null : bytes.ToArray();
    public Memory<byte> Serialize(in byte[] value) => value;
  }

  [Test]
  public async Task RawOnlyDatabaseSupportsConcurrentWritesMaintenanceAndDrop()
  {
    var options = Options(new RawColumnDefinition<int, string>("cache"));
    using var cluster = Factory.CreateInProcess(options);
    var cache = cluster.GetRawColumn<int, string>("cache");
    Parallel.For(0, 500, key => cache.Upsert(key, key.ToString()));
    Assert.That(cluster.Maintenance.ActiveTransactionCount, Is.Zero);
    using (var maintainer = cluster.CreateMaintainer())
    {
      maintainer.EnableJobForCleaningInactiveCaches = false;
      maintainer.EvictToDisk();
      await maintainer.WaitForBackgroundThreadsAsync();
    }
    cluster.Maintenance.SaveMetaData();
    Assert.That(cache.TryGet(499, out var value), Is.True);
    Assert.That(value, Is.EqualTo("499"));
    cluster.Maintenance.Drop();
    using var recreated = Factory.CreateInProcess(options);
    Assert.That(recreated.GetRawColumn<int, string>("cache").ContainsKey(499), Is.False);
  }
}
