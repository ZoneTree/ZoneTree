using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.Transactional.Distributed;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class DistributedRawColumnTests
{
  string DataDirectory;
  readonly DistributedDatabaseFactory Factory = new();

  [SetUp]
  public void SetUp()
  {
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "ZoneTree.sln")))
      root = root.Parent;
    Assert.That(root, Is.Not.Null);
    DataDirectory = Path.Combine(root.FullName, ".tmp", "distributed-raw", "tests", Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(DataDirectory))
      Directory.Delete(DataDirectory, recursive: true);
  }

  DistributedDatabaseOptions Options(params DistributedColumnDefinition[] columns) => new()
  {
    DataDirectory = DataDirectory,
    Logger = SilentTestLogger.Instance,
    Columns = columns.Length != 0 ? columns :
    [
      new DistributedColumnDefinition<int, int>("balances"),
      new DistributedRawColumnDefinition<int, string>("cache")
    ]
  };

  [Test]
  public void RawWritesAreImmediateAndIndependentOfTransactionRollback()
  {
    using var database = Factory.CreateInProcess(Options());
    var balances = database.GetColumn<int, int>("balances");
    var cache = database.GetRawColumn<int, string>("cache");
    Assert.That(cache.Database, Is.SameAs(database));
    Assert.That(cache, Is.Not.InstanceOf<IDistributedColumn<int, string>>());
    using (var transaction = database.BeginTransaction())
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
    Assert.That(database.Maintenance.ActiveTransactionCount, Is.Zero);
    Assert.That(database.Maintenance.LastCommitTimestamp, Is.Zero);
    Assert.That(database.Maintenance.ConflictIndexCount, Is.Zero);
    Assert.Throws<InvalidOperationException>(() => database.GetColumn<int, string>("cache"));
    Assert.Throws<InvalidOperationException>(() => database.GetRawColumn<int, int>("balances"));
  }

  [Test]
  public async Task RawAsyncOperationsAndRangesPreserveDirectSemantics()
  {
    using var database = Factory.CreateInProcess(Options());
    var cache = database.GetRawColumn<int, string>("cache");
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
    using var database = Factory.CreateInProcess(Options());
    var cache = database.GetRawColumn<int, string>("cache");
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
    var database = Factory.CreateInProcess(Options());
    var cache = database.GetRawColumn<int, string>("cache");
    try
    {
      cache.Upsert(1, "value");
      using var existing = database.BeginTransaction();
      database.IsReadOnly = true;
      Assert.Throws<ZoneTreeIsReadOnlyException>(() => cache.Upsert(2, "blocked"));
      Assert.Throws<ZoneTreeIsReadOnlyException>(() => cache.Delete(1));
      Assert.Throws<ZoneTreeIsReadOnlyException>(() => database.BeginTransaction());
      Assert.That(cache.ContainsKey(1), Is.True);
      existing.GetColumn(database.GetColumn<int, int>("balances")).Upsert(1, 100);
      existing.Commit();
      database.IsReadOnly = false;
      cache.Delete(1);
      Assert.That(cache.ContainsKey(1), Is.False);
    }
    finally { database.Dispose(); }
    Assert.Throws<ObjectDisposedException>(() => cache.TryGet(1, out _));
    Assert.Throws<ObjectDisposedException>(() => cache.Upsert(1, "closed"));
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task MixedColumnsCanSyncAndReopen(WriteAheadLogMode mode)
  {
    var options = Options(new DistributedColumnDefinition<int, int>("balances"),
        new DistributedRawColumnDefinition<int, string>("cache")
        {
          ConfigureStorage = factory => factory.ConfigureWriteAheadLogOptions(wal => wal.WriteAheadLogMode = mode)
        });
    using (var database = Factory.CreateInProcess(options))
    {
      database.GetColumn<int, int>("balances").Upsert(1, 100);
      database.GetRawColumn<int, string>("cache").Upsert(1, "value");
      await database.SyncWALAsync();
    }
    using var reopened = Factory.OpenInProcess(options);
    Assert.That(reopened.GetColumn<int, int>("balances").TryGet(1, out var balance), Is.True);
    Assert.That(balance, Is.EqualTo(100));
    Assert.That(reopened.GetRawColumn<int, string>("cache").TryGet(1, out var value), Is.True);
    Assert.That(value, Is.EqualTo("value"));
    Assert.That(reopened.Maintenance.ColumnNames, Is.EqualTo(new[] { "balances", "cache" }));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void ReopeningWithADifferentColumnKindFailsBeforeOpeningItsTree(bool originallyRaw)
  {
    DistributedColumnDefinition Definition(bool raw) => raw
        ? new DistributedRawColumnDefinition<int, string>("data")
        : new DistributedColumnDefinition<int, string>("data");
    var original = Options(Definition(originallyRaw));
    using (Factory.CreateInProcess(original)) { }
    Assert.Throws<InvalidDataException>(() => Factory.OpenOrCreateInProcess(Options(Definition(!originallyRaw))));
    using var reopened = Factory.OpenInProcess(original);
    Assert.That(reopened.Maintenance.ColumnNames, Is.EqualTo(new[] { "data" }));
  }

  [Test]
  public void LegacyTransactionalSchemaStillOpens()
  {
    var options = Options(new DistributedColumnDefinition<int, int>("balances"));
    using (var database = Factory.CreateInProcess(options))
      database.GetColumn<int, int>("balances").Upsert(1, 100);
    using (var writer = new BinaryWriter(File.Create(Path.Combine(DataDirectory, "database.schema"))))
    {
      writer.Write(1);
      writer.Write(1);
      writer.Write("balances");
      writer.Write(typeof(int).FullName);
      writer.Write(typeof(int).FullName);
    }
    using var reopened = Factory.OpenInProcess(options);
    Assert.That(reopened.GetColumn<int, int>("balances").TryGet(1, out var balance), Is.True);
    Assert.That(balance, Is.EqualTo(100));
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
    using var database = Factory.CreateInProcess(Options(
        new DistributedColumnDefinition<int, int>("balances")
        {
          ConfigureStorage = factory => Configure(factory, transactionalFiles, "tx-wal")
        },
        new DistributedRawColumnDefinition<int, string>("cache")
        {
          ConfigureStorage = factory => Configure(factory, rawFiles, "raw-wal")
        }));
    var cache = database.GetRawColumn<int, string>("cache");
    database.GetColumn<int, int>("balances").Upsert(1, 100);
    cache.Upsert(1, "first");
    var beforeTransactional = transactionalFiles.DiskSyncCount;
    var beforeRaw = rawFiles.DiskSyncCount;
    await database.SyncWALAsync();
    Assert.That(transactionalFiles.DiskSyncCount, Is.GreaterThan(beforeTransactional));
    Assert.That(rawFiles.DiskSyncCount, Is.GreaterThan(beforeRaw));
    cache.Upsert(1, "second");
    beforeRaw = rawFiles.DiskSyncCount;
    database.AutoSyncInterval = TimeSpan.FromMilliseconds(5);
    Assert.That(SpinWait.SpinUntil(() => rawFiles.DiskSyncCount > beforeRaw, TimeSpan.FromSeconds(5)), Is.True);
    database.AutoSyncInterval = TimeSpan.Zero;
  }

  [Test]
  public async Task RawByteArraysCanUseAnExplicitSerializerAndReopen()
  {
    var options = Options(new DistributedRawColumnDefinition<string, byte[]>("cache")
    {
      ConfigureStorage = factory => factory.SetValueSerializer(new BytesSerializer())
    });
    using (var database = Factory.CreateInProcess(options))
    {
      var cache = database.GetRawColumn<string, byte[]>("cache");
      cache.Upsert("keep", [1, 2, 3]);
      cache.Upsert("remove", [9]);
      cache.Delete("remove");
      await database.SyncWALAsync();
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
    var options = Options(new DistributedRawColumnDefinition<int, string>("cache"));
    using var database = Factory.CreateInProcess(options);
    var cache = database.GetRawColumn<int, string>("cache");
    Parallel.For(0, 500, key => cache.Upsert(key, key.ToString()));
    Assert.That(database.Maintenance.ActiveTransactionCount, Is.Zero);
    using (var maintainer = database.CreateMaintainer())
    {
      maintainer.EnableJobForCleaningInactiveCaches = false;
      maintainer.EvictToDisk();
      await maintainer.WaitForBackgroundThreadsAsync();
    }
    database.Maintenance.SaveMetaData();
    Assert.That(cache.TryGet(499, out var value), Is.True);
    Assert.That(value, Is.EqualTo("499"));
    database.Maintenance.Drop();
    using var recreated = Factory.CreateInProcess(options);
    Assert.That(recreated.GetRawColumn<int, string>("cache").ContainsKey(499), Is.False);
  }
}
