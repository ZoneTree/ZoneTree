using ZoneTree.AbstractFileStream;
using ZoneTree.Backup;
using ZoneTree.Options;
using ZoneTree.Segments.Disk;

namespace ZoneTree.UnitTests;

public sealed class TransactionalLiveBackupTests
{
  [TestCase(WriteAheadLogMode.Shared, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(WriteAheadLogMode.AsyncShared, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(WriteAheadLogMode.Shared, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(WriteAheadLogMode.AsyncShared, DiskSegmentMode.MultiPartDiskSegment)]
  public async Task SnapshotSurvivesConcurrentCommitsRollbacksRotationsAndMerges(WriteAheadLogMode wal, DiskSegmentMode disk)
  {
    using var fixture = new Fixture(wal, disk);
    using var tree = fixture.Factory("source").CreateTransactional();
    using (var tx = tree.BeginTransaction())
    { for (var key = 0; key < 50; ++key) tx.Upsert(key, key + 100); tx.Commit(); }
    var maintenance = tree.Maintenance.ZoneTree.Maintenance;
    maintenance.MoveMutableSegmentForward();
    maintenance.StartMergeOperation()?.Join();
    using var oldSnapshot = tree.BeginReadOnlyTransaction();
    using (var tx = tree.BeginTransaction()) { tx.Delete(5); tx.Upsert(6, 600); tx.Commit(); }
    using var active = tree.BeginTransaction();
    active.Upsert(0, 999);
    active.Upsert(100, 999);
    using (var rollback = tree.BeginTransaction()) { rollback.Upsert(200, 999); rollback.Rollback(); }

    var store = new PausedStore(fixture.BackupPath);
    using var backup = tree.CreateLiveBackup(new LiveBackupOptions { Store = store, BackupAfterMerge = false });
    var copying = backup.CreateGenerationAsync();
    await store.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try
    {
      await Task.Run(() =>
      {
        active.Upsert(101, 999);
        active.Commit();
        using var later = tree.BeginTransaction();
        later.Upsert(6, 666);
        later.Upsert(300, 999);
        later.Commit();
        maintenance.MoveMutableSegmentForward();
        maintenance.StartMergeOperation()?.Join();
        maintenance.StartBottomSegmentsMergeOperation(0, int.MaxValue)?.Join();
      }).WaitAsync(TimeSpan.FromSeconds(10));
    }
    finally { store.Release.TrySetResult(true); }
    await copying.WaitAsync(TimeSpan.FromSeconds(10));
    var catalog = new LocalLiveBackupProvider(fixture.BackupPath).ReadCurrentGeneration();
    Assert.That(catalog.TreeKind, Is.EqualTo(LiveBackupTreeKind.Transactional));
    Assert.That(catalog.Version, Is.EqualTo(3));
    Assert.That(catalog.Files, Is.Not.Empty, "Transactional backup must retain physical disk-file reuse.");
    Assert.That(oldSnapshot.TryGet(6, out var old) && old == 106, Is.True);

    using (var restored = await fixture.Factory("restored").RestoreTransactionalFromLatestLiveBackup(fixture.BackupPath))
    {
      using (var read = restored.BeginReadOnlyTransaction())
      {
        Assert.That(read.Range(int.MinValue, int.MaxValue).Count(), Is.EqualTo(49));
        for (var key = 0; key < 50; ++key)
        {
          if (key == 5) { Assert.That(read.ContainsKey(key), Is.False); continue; }
          Assert.That(read.TryGet(key, out var value) && value == (key == 6 ? 600 : key + 100), Is.True, $"key {key}");
        }
        foreach (var key in new[] { 100, 101, 200, 300 }) Assert.That(read.ContainsKey(key), Is.False);
      }
      using var next = restored.BeginTransaction();
      next.Upsert(400, 400);
      await next.CommitDurableAsync();
    }
    using var reopened = fixture.Factory("restored").OpenTransactional();
    using var current = reopened.BeginReadOnlyTransaction();
    Assert.That(current.TryGet(400, out var persisted) && persisted == 400, Is.True);
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task BackupExcludesPreparedDurableCommitWithoutWaitingForItsSync(WriteAheadLogMode wal)
  {
    using var fixture = new Fixture(wal, DiskSegmentMode.SingleDiskSegment);
    var files = new PowerLossFileStreamProvider();
    using var tree = fixture.Factory("memory-source", files).CreateTransactional();
    using (var tx = tree.BeginTransaction()) { tx.Upsert(1, 10); await tx.CommitDurableAsync(); }
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    files.BeforeDiskSync = path =>
    {
      if (!path.EndsWith(".wal", StringComparison.Ordinal) || !path.Replace('\\', '/').Contains("/shared/", StringComparison.Ordinal)) return;
      entered.Set();
      if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException();
    };
    using var prepared = tree.BeginTransaction();
    prepared.Upsert(1, 20);
    var committing = prepared.CommitDurableAsync();
    using var backup = tree.CreateLiveBackup(fixture.BackupPath);
    try
    {
      Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
      await Task.Run(() => backup.CreateGenerationAsync()).WaitAsync(TimeSpan.FromSeconds(10));
      using var restored = await fixture.Factory("restored").RestoreTransactionalFromLatestLiveBackup(fixture.BackupPath);
      using var read = restored.BeginReadOnlyTransaction();
      Assert.That(read.TryGet(1, out var value) && value == 10, Is.True);
    }
    finally { release.Set(); files.BeforeDiskSync = null; }
    await committing;
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task RestoreDoesNotRegisterFinishedRollbacksOrTransactionsWithoutWrites(WriteAheadLogMode wal)
  {
    using var fixture = new Fixture(wal, DiskSegmentMode.SingleDiskSegment);
    using var tree = fixture.Factory("source").CreateTransactional();
    using (var tx = tree.BeginTransaction()) { tx.Upsert(1, 10); tx.Commit(); }
    // An older transaction without writes keeps the finished rollback registered.
    using var older = tree.BeginTransaction();
    using (var rollback = tree.BeginTransaction()) { rollback.Upsert(2, 20); rollback.Rollback(); }
    Assert.That(tree.Maintenance.AbortedTransactionCount, Is.EqualTo(1));
    using (var backup = tree.CreateLiveBackup(fixture.BackupPath))
      await backup.CreateGenerationAsync();

    using var restored = await fixture.Factory("restored").RestoreTransactionalFromLatestLiveBackup(fixture.BackupPath);
    Assert.That(restored.Maintenance.AbortedTransactionCount, Is.Zero);
    using var read = restored.BeginReadOnlyTransaction();
    Assert.That(read.TryGet(1, out var value) && value == 10, Is.True);
    Assert.That(read.ContainsKey(2), Is.False);
  }

  [Test]
  public void TransactionalBackupRejectsExcludingMemoryRecords()
  {
    using var fixture = new Fixture(WriteAheadLogMode.Shared, DiskSegmentMode.SingleDiskSegment);
    using var tree = fixture.Factory("source").CreateTransactional();
    Assert.Throws<ArgumentException>(() => tree.CreateLiveBackup(new LiveBackupOptions
    { Store = new LocalLiveBackupProvider(fixture.BackupPath), IncludeInMemoryRecords = false }));
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task RepeatedBackupsRecoverWholeTransactionsDuringContinuousWrites(WriteAheadLogMode wal)
  {
    using var fixture = new Fixture(wal, DiskSegmentMode.MultiPartDiskSegment);
    using var tree = fixture.Factory("source").CreateTransactional();
    using var maintainer = tree.CreateMaintainer();
    using (var initial = tree.BeginTransaction())
    {
      for (var key = 0; key < 8; ++key) initial.Upsert(key, 0);
      initial.Commit();
    }
    using var stop = new CancellationTokenSource();
    var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var writer = Task.Run(async () =>
    {
      for (var epoch = 1; epoch <= 120 && !stop.IsCancellationRequested; ++epoch)
      {
        using var tx = tree.BeginTransaction();
        for (var key = 0; key < 8; ++key) tx.Upsert(key, epoch);
        if (epoch % 7 == 0) tx.Rollback();
        else if (epoch % 5 == 0) await tx.CommitDurableAsync();
        else tx.Commit();
        started.TrySetResult(true);
        await Task.Delay(1);
      }
    });
    try
    {
      await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
      using var backup = tree.CreateLiveBackup(new LiveBackupOptions
      { Store = new LocalLiveBackupProvider(fixture.BackupPath), BackupAfterMerge = false });
      for (var generation = 1; generation <= 4; ++generation)
      {
        await backup.CreateGenerationAsync().WaitAsync(TimeSpan.FromSeconds(20));
        using var restored = await fixture.Factory($"restored-{generation}")
            .RestoreTransactionalFromLiveBackupGeneration(fixture.BackupPath, backup.CurrentGenerationId);
        using var read = restored.BeginReadOnlyTransaction();
        var records = read.Range(int.MinValue, int.MaxValue).ToArray();
        Assert.That(records.Select(x => x.Key), Is.EqualTo(Enumerable.Range(0, 8)));
        Assert.That(records.Select(x => x.Value).Distinct().Count(), Is.EqualTo(1), "A backup must not split a transaction.");
        var epoch = records[0].Value;
        Assert.That(epoch == 0 || epoch % 7 != 0, Is.True, "Rolled-back versions must stay invisible.");
      }
    }
    finally
    {
      stop.Cancel();
      await writer;
      await maintainer.WaitForBackgroundThreadsAsync();
    }
  }

  [Test]
  public async Task RestoreRejectsWrongTreeKindAndReleasesItsLease()
  {
    using var fixture = new Fixture(WriteAheadLogMode.Shared, DiskSegmentMode.SingleDiskSegment);
    using var tree = fixture.Factory("source").CreateTransactional();
    using var backup = tree.CreateLiveBackup(fixture.BackupPath);
    await backup.CreateGenerationAsync();
    Assert.ThrowsAsync<LiveBackupIntegrityException>(async () =>
    { using var wrong = await fixture.Factory("wrong").RestoreFromLatestLiveBackup(fixture.BackupPath); });
    var catalog = Directory.GetFiles(Path.Combine(fixture.BackupPath, "generations"), "*.json").Single();
    using var exclusive = new FileStream(catalog, FileMode.Open, FileAccess.Read, FileShare.None);
  }

  sealed class PausedStore(string path) : ILiveBackupStore
  {
    readonly LocalLiveBackupProvider Inner = new(path);
    public TaskCompletionSource<bool> Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<long> GetNextGenerationIdAsync(CancellationToken token) => Inner.GetNextGenerationIdAsync(token);
    public async Task BeginGenerationAsync(long id, DateTime utc, LiveBackupTreeKind kind, CancellationToken token)
    { await Inner.BeginGenerationAsync(id, utc, kind, token); Captured.TrySetResult(true); await Release.Task.WaitAsync(token); }
    public Task<bool> UseSegmentAsync(long id, DiskSegmentFile file, CancellationToken token) => Inner.UseSegmentAsync(id, file, token);
    public Task UploadSegmentFileAsync(long id, DiskSegmentFile file, Stream stream, CancellationToken token) => Inner.UploadSegmentFileAsync(id, file, stream, token);
    public Task<ILiveBackupRecordWriter> OpenRecordWriterAsync(long id, LiveBackupRecordBatch batch, CancellationToken token) => Inner.OpenRecordWriterAsync(id, batch, token);
    public Task CompleteGenerationAsync(long id, long index, CancellationToken token) => Inner.CompleteGenerationAsync(id, index, token);
  }

  sealed class Fixture(WriteAheadLogMode wal, DiskSegmentMode disk) : IDisposable
  {
    readonly string Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, nameof(TransactionalLiveBackupTests), Guid.NewGuid().ToString("N"));
    public string BackupPath => Path.Combine(Root, "backup");
    public ZoneTreeFactory<int, int> Factory(string name, IFileStreamProvider files = null) => new ZoneTreeFactory<int, int>(files)
        .SetDataDirectory(Path.Combine(Root, name))
        .Configure(o => o.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(17)
        .ConfigureDiskSegmentOptions(o => { o.DiskSegmentMode = disk; o.MinimumRecordCount = 4; o.MaximumRecordCount = 8; })
        .ConfigureWriteAheadLogOptions(o => { o.WriteAheadLogMode = wal; o.CompressionBlockSize = 1024; o.SharedModeOptions.MaximumChunkSize = 4096; });
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
  }
}
