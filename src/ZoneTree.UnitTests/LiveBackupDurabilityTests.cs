using ZoneTree.AbstractFileStream;
using ZoneTree.Backup;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class LiveBackupDurabilityTests
{
  [TestCase(false)]
  [TestCase(true)]
  public async Task PublicationPersistsDependenciesBeforeRetention(bool transactional)
  {
    using var fixture = new Fixture(transactional);
    await fixture.Backup();
    var first = new LocalLiveBackupProvider(fixture.BackupPath).ReadCurrentGeneration();
    fixture.Advance();
    var probe = new PublicationProbe(fixture.BackupPath);
    probe.BeforeSync = stage =>
    {
      if (stage == "file:manifest" || stage == "directory:manifest" && !probe.ManifestDirectorySynced)
        Assert.That(File.Exists(fixture.CatalogPath(1)), Is.True, "Do not retire the fallback before publication is durable.");
      if (stage == "directory:retire" && probe.CatalogDirectorySyncs == 1)
        Assert.That(File.Exists(Path.Combine(fixture.BackupPath, first.RecordBatch.BackupPath)), Is.True,
            "Persist catalog retirement before deleting its data.");
    };
    await fixture.Backup(probe.Store);
    Assert.That(probe.ManifestDirectorySynced, Is.True);
    Assert.That(probe.CatalogDirectorySyncs, Is.GreaterThanOrEqualTo(2));
    Assert.That(File.Exists(fixture.CatalogPath(1)), Is.False);
    await fixture.AssertRestores(2);
  }

  [TestCase("file:data")]
  [TestCase("file:records")]
  [TestCase("directory:data")]
  [TestCase("directory:records")]
  [TestCase("file:catalog")]
  [TestCase("directory:catalog")]
  [TestCase("file:manifest")]
  [TestCase("directory:manifest")]
  [TestCase("directory:retire")]
  public async Task SyncFailureStopsDependentPublicationAndCanBeRetried(string failure)
  {
    using var fixture = new Fixture(transactional: true);
    await fixture.Backup();
    var first = new LocalLiveBackupProvider(fixture.BackupPath).ReadCurrentGeneration();
    fixture.Advance();
    var probe = new PublicationProbe(fixture.BackupPath);
    var failed = false;
    probe.BeforeSync = stage =>
    {
      if (stage != failure) return;
      failed = true;
      throw new IOException("Injected sync failure: " + stage);
    };
    using var backup = fixture.CreateBackup(probe.Store);
    Assert.ThrowsAsync<LiveBackupGenerationException>(() => backup.CreateGenerationAsync());
    Assert.That(failed, Is.True);
    Assert.That(File.Exists(Path.Combine(fixture.BackupPath, first.RecordBatch.BackupPath)), Is.True);
    if (failure == "directory:retire")
      await fixture.AssertRestores(2); // The new head was durable before retirement began.
    else
    {
      Assert.That(File.Exists(fixture.CatalogPath(1)), Is.True);
      await fixture.AssertRestores(1, advanced: false);
    }
    probe.BeforeSync = null;
    probe.ResetPublication();
    await backup.CreateGenerationAsync();
    Assert.That(backup.CurrentGenerationId, Is.EqualTo(3));
    await fixture.AssertRestores(3);
    Assert.That(File.Exists(fixture.CatalogPath(1)), Is.False);
  }

  [TestCase(false, DiskSegmentMode.SingleDiskSegment, WriteAheadLogMode.Shared)]
  [TestCase(false, DiskSegmentMode.MultiPartDiskSegment, WriteAheadLogMode.AsyncShared)]
  [TestCase(false, DiskSegmentMode.SingleDiskSegment, WriteAheadLogMode.Sync)]
  [TestCase(false, DiskSegmentMode.MultiPartDiskSegment, WriteAheadLogMode.SyncCompressed)]
  [TestCase(false, DiskSegmentMode.SingleDiskSegment, WriteAheadLogMode.AsyncCompressed)]
  [TestCase(true, DiskSegmentMode.SingleDiskSegment, WriteAheadLogMode.Shared)]
  [TestCase(true, DiskSegmentMode.MultiPartDiskSegment, WriteAheadLogMode.AsyncShared)]
  public async Task RestoredDiskAndMemoryRecordsSurvivePowerLossBeforeClose(bool transactional, DiskSegmentMode disk, WriteAheadLogMode wal)
  {
    using var fixture = new Fixture(transactional, disk);
    fixture.Advance();
    await fixture.Backup();
    var files = new PowerLossFileStreamProvider();
    var catalog = new LocalLiveBackupProvider(fixture.BackupPath).ReadCurrentGeneration();
    Assert.That(catalog.Files, Is.Not.Empty);
    var dataPath = Path.Combine(fixture.Root, "simulated", "data");
    var observedCopies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var dataDirectorySynced = false;
    var walDirectorySynced = false;
    var metadataSynced = false;
    files.BeforeDirectorySync = path =>
    {
      if (path == dataPath.Replace('\\', '/')) dataDirectorySynced = true;
      if (path == (dataPath + "-wal/seg").Replace('\\', '/')) walDirectorySynced = true;
    };
    files.BeforeDiskSync = path =>
    {
      if (catalog.Files.Any(x => path.Replace('\\', '/').EndsWith('/' + x.FileName, StringComparison.Ordinal)))
        observedCopies.Add(path);
      if (path.Replace('\\', '/').Contains("/data/0.json.", StringComparison.Ordinal))
      {
        Assert.That(observedCopies.Count, Is.EqualTo(catalog.Files.Count), "All immutable copies must be synced before metadata.");
        Assert.That(dataDirectorySynced && walDirectorySynced, Is.True, "Restore file names must be durable before metadata.");
        metadataSynced = true;
      }
    };
    // Keep the restored tree open: disposal would add syncs and conceal a missing restore barrier.
    if (transactional)
    {
      using var restored = await fixture.Factory(dataPath, files, wal)
          .RestoreTransactionalFromLatestLiveBackup(fixture.BackupPath);
      using (var tx = restored.BeginTransaction()) { tx.Upsert(99, 9999); await tx.CommitDurableAsync(); }
      using var recovered = fixture.Factory(dataPath, files.SimulatePowerLoss(), wal).OpenTransactional();
      using var read = recovered.BeginReadOnlyTransaction();
      Assert.That(read.Range(int.MinValue, int.MaxValue).Count(), Is.EqualTo(14));
      for (var key = 0; key < 12; ++key)
        Assert.That(read.TryGet(key, out var value) && value == (key == 0 ? 2000 : key + 100), Is.True, $"key {key}");
      Assert.That(read.TryGet(50, out var recent) && recent == 5000, Is.True);
      Assert.That(read.TryGet(99, out var durable) && durable == 9999, Is.True);
    }
    else
    {
      using var restored = await fixture.Factory(dataPath, files, wal).RestoreFromLatestLiveBackup(fixture.BackupPath);
      using var recovered = fixture.Factory(dataPath, files.SimulatePowerLoss(), wal).Open();
      Assert.That(recovered.CountFullScan(), Is.EqualTo(13));
      for (var key = 0; key < 12; ++key)
        Assert.That(recovered.TryGet(key, out var value) && value == (key == 0 ? 2000 : key + 100), Is.True, $"key {key}");
      Assert.That(recovered.TryGet(50, out var recent) && recent == 5000, Is.True);
    }
    Assert.That(observedCopies.Count, Is.EqualTo(catalog.Files.Count));
    Assert.That(metadataSynced, Is.True);
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task RestoreSyncFailureDoesNotPublishMetadata(bool directoryFailure)
  {
    using var fixture = new Fixture(transactional: true);
    fixture.Advance();
    await fixture.Backup();
    var files = new PowerLossFileStreamProvider();
    var path = Path.Combine(fixture.Root, "failed-restore");
    if (directoryFailure)
      files.BeforeDirectorySync = name =>
      { if (name == path.Replace('\\', '/')) throw new IOException("Injected directory sync failure."); };
    // Use actual catalog filenames rather than relying on a particular segment suffix.
    var catalog = new LocalLiveBackupProvider(fixture.BackupPath).ReadCurrentGeneration();
    if (!directoryFailure)
      files.FailDiskSync = name => catalog.Files.Any(x => name.EndsWith('/' + x.FileName, StringComparison.Ordinal));
    Assert.ThrowsAsync<IOException>(async () =>
    { using var restored = await fixture.Factory(path, files).RestoreTransactionalFromLatestLiveBackup(fixture.BackupPath); });
    Assert.That(files.FileExists(Path.Combine(path, "0.json")), Is.False);
  }

  sealed class PublicationProbe
  {
    readonly string Root;
    readonly object Gate = new();
    bool DataFileSynced, RecordsFileSynced, DataDirectorySynced, RecordsDirectorySynced, CatalogFileSynced, ManifestFileSynced;
    public int CatalogDirectorySyncs { get; private set; }
    public bool ManifestDirectorySynced { get; private set; }
    public Action<string> BeforeSync { get; set; }
    public LocalLiveBackupProvider Store { get; }
    public PublicationProbe(string path)
    {
      Root = Path.GetFullPath(path);
      Store = new LocalLiveBackupProvider(new LocalLiveBackupOptions { Directory = path, KeepLastGenerations = 1 }, SyncFile, SyncDirectory);
    }
    public void ResetPublication()
    {
      DataDirectorySynced = RecordsDirectorySynced = CatalogFileSynced = ManifestFileSynced = ManifestDirectorySynced = false;
      CatalogDirectorySyncs = 0;
    }
    void SyncFile(FileStream stream)
    {
      lock (Gate)
      {
        var parent = Path.GetDirectoryName(stream.Name);
        var kind = parent == Path.Combine(Root, "data") ? "data" : parent == Path.Combine(Root, "records") ? "records" :
            parent == Path.Combine(Root, "generations") ? "catalog" : "manifest";
        if (kind == "catalog") Assert.That(DataDirectorySynced && RecordsDirectorySynced, Is.True);
        if (kind == "manifest") Assert.That(CatalogDirectorySyncs, Is.GreaterThan(0));
        BeforeSync?.Invoke("file:" + kind);
        stream.Flush(true);
        switch (kind)
        {
          case "data": DataFileSynced = true; break;
          case "records": RecordsFileSynced = true; break;
          case "catalog": CatalogFileSynced = true; break;
          case "manifest": ManifestFileSynced = true; break;
        }
      }
    }
    void SyncDirectory(string path)
    {
      lock (Gate)
      {
        var kind = path == Path.Combine(Root, "data") ? "data" : path == Path.Combine(Root, "records") ? "records" :
            path == Path.Combine(Root, "generations") ? (CatalogDirectorySyncs == 0 ? "catalog" : "retire") :
            path == Root && ManifestFileSynced ? "manifest" : "ancestor";
        if (kind == "data") Assert.That(DataFileSynced, Is.True);
        if (kind == "records") Assert.That(RecordsFileSynced, Is.True);
        if (kind == "catalog") Assert.That(CatalogFileSynced, Is.True);
        if (kind == "retire") Assert.That(ManifestDirectorySynced, Is.True);
        BeforeSync?.Invoke("directory:" + kind);
        DirectorySync.Flush(path);
        switch (kind)
        {
          case "data": DataDirectorySynced = true; break;
          case "records": RecordsDirectorySynced = true; break;
          case "catalog": case "retire": ++CatalogDirectorySyncs; break;
          case "manifest": ManifestDirectorySynced = true; break;
        }
      }
    }
  }

  sealed class Fixture : IDisposable
  {
    public string Root { get; } = Path.Combine(TestContext.CurrentContext.WorkDirectory, nameof(LiveBackupDurabilityTests), Guid.NewGuid().ToString("N"));
    public string BackupPath => Path.Combine(Root, "backup");
    readonly bool Transactional;
    readonly DiskSegmentMode Disk;
    readonly IZoneTree<int, int> Plain;
    readonly ITransactionalZoneTree<int, int> Transactions;
    public Fixture(bool transactional, DiskSegmentMode disk = DiskSegmentMode.SingleDiskSegment)
    {
      Transactional = transactional; Disk = disk;
      if (transactional) Transactions = Factory(Path.Combine(Root, "source")).CreateTransactional();
      else Plain = Factory(Path.Combine(Root, "source")).Create();
      for (var i = 0; i < 12; ++i) Write(i, i + 100);
      Merge();
    }
    public string CatalogPath(long id) => Path.Combine(BackupPath, "generations", id.ToString("D20") + ".json");
    public ZoneTreeFactory<int, int> Factory(string path, IFileStreamProvider files = null, WriteAheadLogMode wal = WriteAheadLogMode.AsyncShared)
        => new ZoneTreeFactory<int, int>(files).SetDataDirectory(path).SetWriteAheadLogDirectory(path + "-wal")
        .DisableDeletion().Configure(o => o.AllowUnsafeOptionValues = true).SetMutableSegmentMaxItemCount(1000)
        .ConfigureDiskSegmentOptions(o => { o.DiskSegmentMode = Disk; o.MinimumRecordCount = 2; o.MaximumRecordCount = 4; })
        .ConfigureWriteAheadLogOptions(o => { o.WriteAheadLogMode = wal; o.CompressionBlockSize = 1024; });
    void Write(int key, int value)
    {
      if (Plain != null) Plain.Upsert(key, value);
      else { using var tx = Transactions.BeginTransaction(); tx.Upsert(key, value); tx.Commit(); }
    }
    void Merge()
    {
      if (Plain != null) { Plain.Maintenance.MoveMutableSegmentForward(); Plain.Maintenance.StartMergeOperation()?.Join(); }
      else { var m = Transactions.Maintenance.ZoneTree.Maintenance; m.MoveMutableSegmentForward(); m.StartMergeOperation()?.Join(); }
    }
    public void Advance() { Write(0, 2000); Merge(); Write(50, 5000); }
    public ILiveBackup CreateBackup(ILiveBackupStore store)
    {
      var options = new LiveBackupOptions { Store = store, BackupAfterMerge = false, InMemoryMode = LiveBackupInMemoryMode.Snapshot };
      return Transactional ? Transactions.CreateLiveBackup(options) : Plain.CreateLiveBackup(options);
    }
    public async Task Backup(ILiveBackupStore store = null)
    {
      using var backup = CreateBackup(store ?? new LocalLiveBackupProvider(new LocalLiveBackupOptions { Directory = BackupPath, KeepLastGenerations = 1 }));
      await backup.CreateGenerationAsync();
    }
    public async Task AssertRestores(long generation, bool advanced = true)
    {
      var path = Path.Combine(Root, "check-" + Guid.NewGuid().ToString("N"));
      if (Transactional)
      {
        using var restored = await Factory(path).RestoreTransactionalFromLiveBackupGeneration(BackupPath, generation);
        using var read = restored.BeginReadOnlyTransaction();
        Assert.That(read.TryGet(0, out var zero) && zero == (advanced ? 2000 : 100), Is.True);
        Assert.That(read.Range(int.MinValue, int.MaxValue).Count(), Is.EqualTo(advanced ? 13 : 12));
      }
      else
      {
        using var restored = await Factory(path).RestoreFromLiveBackupGeneration(BackupPath, generation);
        Assert.That(restored.TryGet(0, out var zero) && zero == (advanced ? 2000 : 100), Is.True);
        Assert.That(restored.CountFullScan(), Is.EqualTo(advanced ? 13 : 12));
      }
    }
    public void Dispose() { Plain?.Dispose(); Transactions?.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
  }
}
