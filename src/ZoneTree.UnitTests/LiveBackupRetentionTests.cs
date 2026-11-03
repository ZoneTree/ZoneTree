using System.Text.Json;
using ZoneTree.Backup;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class LiveBackupRetentionTests
{
  [TestCase(false, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(true, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(false, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(true, DiskSegmentMode.MultiPartDiskSegment)]
  public async Task RetainedGenerationsRestoreAndProtectSharedFiles(bool transactional, DiskSegmentMode disk)
  {
    using var fixture = new Fixture(transactional, disk, keep: 2);
    fixture.Merge();
    await fixture.Backup();
    var original = fixture.Catalog(1);
    fixture.Write(0, 1000);
    fixture.Write(100, 1000);
    await fixture.Backup();
    fixture.Write(1, 2000);
    fixture.Merge();
    await fixture.Backup();

    Assert.That(fixture.GenerationIds(), Is.EqualTo(new[] { 2L, 3L }));
    var retained = new[] { fixture.Catalog(2), fixture.Catalog(3) };
    var expectedPaths = retained.SelectMany(x => x.Files).Select(x => fixture.PathOf(x.BackupPath)).ToHashSet();
    Assert.That(Directory.GetFiles(fixture.PathOf("data")).ToHashSet().SetEquals(expectedPaths), Is.True);
    Assert.That(original.Files.Any(x => retained[0].Files.Any(y => y.BackupPath == x.BackupPath)), Is.True,
        "This scenario must exercise a file shared with the evicted generation.");
    Assert.That(File.Exists(fixture.PathOf(original.RecordBatch.BackupPath)), Is.False);
    await fixture.AssertRestores(2, expectedOne: 101);
    await fixture.AssertRestores(3, expectedOne: 2000);
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task ActiveRestoreLeaseDefersEviction(bool transactional)
  {
    using var fixture = new Fixture(transactional, DiskSegmentMode.MultiPartDiskSegment, keep: 1);
    fixture.Merge();
    await fixture.Backup();
    var source = new LocalLiveBackupProvider(fixture.BackupPath);
    using var leased = await source.ReadGenerationAsync(1, CancellationToken.None);
    var first = fixture.Catalog(1);
    fixture.Write(0, 1000);
    fixture.Write(100, 1000);
    fixture.Merge();
    await fixture.Backup();
    Assert.That(fixture.GenerationIds(), Is.EqualTo(new[] { 1L, 2L }));
    Assert.That(File.Exists(fixture.PathOf(first.RecordBatch.BackupPath)), Is.True);
    await fixture.AssertRestores(1, expectedOne: 101, expectedZero: 100, hasExtra: false);
    using (var latest = await source.ReadLatestGenerationAsync(CancellationToken.None))
      Assert.That(latest.GenerationId, Is.EqualTo(2), "A reused source must refresh the manifest.");
    leased.Dispose();
    await fixture.Backup();
    Assert.That(fixture.GenerationIds(), Is.EqualTo(new[] { 3L }));
    Assert.That(File.Exists(fixture.PathOf(first.RecordBatch.BackupPath)), Is.False);
    await fixture.AssertRestores(3, expectedOne: 101);
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task RetentionReclaimsAbandonedUploadsAndTemporaryFiles(bool transactional)
  {
    using var fixture = new Fixture(transactional, DiskSegmentMode.SingleDiskSegment, keep: 1);
    await fixture.Backup();
    var abandoned = new[] { "data/abandoned.dat", "records/abandoned.bin", "generations/abandoned.json.tmp", "manifest.json.abandoned.tmp" };
    foreach (var path in abandoned) File.WriteAllText(fixture.PathOf(path), "incomplete");
    fixture.Write(0, 1000);
    fixture.Write(100, 1000);
    await fixture.Backup();
    foreach (var path in abandoned) Assert.That(File.Exists(fixture.PathOf(path)), Is.False, path);
    await fixture.AssertRestores(2, expectedOne: 101);
  }

  [Test]
  public async Task RestartDoesNotReuseAnUnpublishedCatalogId()
  {
    using var fixture = new Fixture(transactional: true, DiskSegmentMode.SingleDiskSegment, keep: 1);
    await fixture.Backup();
    var orphan = fixture.Catalog(1);
    orphan.GenerationId = 5;
    // Simulate a complete catalog whose manifest publication never happened.
    File.WriteAllText(fixture.PathOf("generations/00000000000000000005.json"), JsonSerializer.Serialize(orphan));
    fixture.Write(0, 1000);
    fixture.Write(100, 1000);
    await fixture.Backup();
    Assert.That(fixture.GenerationIds(), Is.EqualTo(new[] { 6L }));
    Assert.That(new LocalLiveBackupProvider(fixture.BackupPath).ReadCurrentGeneration().GenerationId, Is.EqualTo(6));
    await fixture.AssertRestores(6, expectedOne: 101);
  }

  sealed class Fixture : IDisposable
  {
    readonly string Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, nameof(LiveBackupRetentionTests), Guid.NewGuid().ToString("N"));
    readonly bool Transactional;
    readonly DiskSegmentMode Disk;
    readonly int Keep;
    readonly IZoneTree<int, int> Plain;
    readonly ITransactionalZoneTree<int, int> Transactions;
    public string BackupPath => Path.Combine(Root, "backup");
    public string PathOf(string relative) => Path.GetFullPath(Path.Combine(BackupPath, relative.Replace('/', Path.DirectorySeparatorChar)));
    public LocalLiveBackupGenerationCatalog Catalog(long id) => new LocalLiveBackupProvider(BackupPath).ReadGeneration(id);
    public long[] GenerationIds() => Directory.GetFiles(PathOf("generations"), "*.json")
        .Select(x => long.Parse(Path.GetFileNameWithoutExtension(x))).Order().ToArray();
    ZoneTreeFactory<int, int> Factory(string path) => new ZoneTreeFactory<int, int>().SetDataDirectory(Path.Combine(Root, path))
        .DisableDeletion().Configure(o => o.AllowUnsafeOptionValues = true).SetMutableSegmentMaxItemCount(17)
        .ConfigureDiskSegmentOptions(o => { o.DiskSegmentMode = Disk; o.MinimumRecordCount = 4; o.MaximumRecordCount = 8; })
        .ConfigureWriteAheadLogOptions(o => { o.WriteAheadLogMode = WriteAheadLogMode.AsyncShared; o.CompressionBlockSize = 1024; o.SharedModeOptions.MaximumChunkSize = 4096; });
    public Fixture(bool transactional, DiskSegmentMode disk, int keep)
    {
      Transactional = transactional; Disk = disk; Keep = keep;
      if (transactional) Transactions = Factory("source").CreateTransactional();
      else Plain = Factory("source").Create();
      for (var i = 0; i < 40; ++i) Write(i, i + 100);
    }
    public void Write(int key, int value)
    {
      if (Plain != null) Plain.Upsert(key, value);
      else { using var tx = Transactions.BeginTransaction(); tx.Upsert(key, value); tx.Commit(); }
    }
    public void Merge()
    {
      if (Plain != null) { Plain.Maintenance.MoveMutableSegmentForward(); Plain.Maintenance.StartMergeOperation()?.Join(); }
      else { var m = Transactions.Maintenance.ZoneTree.Maintenance; m.MoveMutableSegmentForward(); m.StartMergeOperation()?.Join(); }
    }
    public async Task Backup()
    {
      var options = new LiveBackupOptions
      {
        Store = new LocalLiveBackupProvider(new LocalLiveBackupOptions { Directory = BackupPath, KeepLastGenerations = Keep }),
        BackupAfterMerge = false, InMemoryMode = LiveBackupInMemoryMode.Snapshot
      };
      using ILiveBackup backup = Transactional ? Transactions.CreateLiveBackup(options) : Plain.CreateLiveBackup(options);
      await backup.CreateGenerationAsync();
    }
    public async Task AssertRestores(long id, int expectedOne, int expectedZero = 1000, bool hasExtra = true)
    {
      var path = "restored-" + Guid.NewGuid().ToString("N");
      if (Transactional)
      {
        using var restored = await Factory(path).RestoreTransactionalFromLiveBackupGeneration(BackupPath, id);
        using var read = restored.BeginReadOnlyTransaction();
        Assert.That(read.TryGet(0, out var zero) && zero == expectedZero, Is.True);
        Assert.That(read.TryGet(1, out var one) && one == expectedOne, Is.True);
        Assert.That(read.ContainsKey(100), Is.EqualTo(hasExtra));
        Assert.That(read.Range(int.MinValue, int.MaxValue).Count(), Is.EqualTo(hasExtra ? 41 : 40));
      }
      else
      {
        using var restored = await Factory(path).RestoreFromLiveBackupGeneration(BackupPath, id);
        Assert.That(restored.TryGet(0, out var zero) && zero == expectedZero, Is.True);
        Assert.That(restored.TryGet(1, out var one) && one == expectedOne, Is.True);
        Assert.That(restored.ContainsKey(100), Is.EqualTo(hasExtra));
        Assert.That(restored.CountFullScan(), Is.EqualTo(hasExtra ? 41 : 40));
      }
    }
    public void Dispose() { Plain?.Dispose(); Transactions?.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
  }
}
