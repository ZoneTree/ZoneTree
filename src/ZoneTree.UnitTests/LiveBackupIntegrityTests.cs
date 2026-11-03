using System.Security.Cryptography;
using System.Text.Json;
using ZoneTree.Backup;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class LiveBackupIntegrityTests
{
  [TestCase(CompressionMethod.None)]
  [TestCase(CompressionMethod.LZ4)]
  [TestCase(CompressionMethod.Zstd)]
  public async Task ChecksumsCoverStoredFilesAndBatches(CompressionMethod codec)
  {
    using var fixture = new BackupFixture(disk: true);
    fixture.Backup(codec);
    var catalog = fixture.Catalog;
    Assert.That(catalog.Version, Is.EqualTo(2));
    foreach (var file in catalog.Files)
      Assert.That(file.Sha256, Is.EqualTo(Digest(File.ReadAllBytes(fixture.PathOf(file.BackupPath)))));
    Assert.That(catalog.RecordBatch.Sha256, Is.EqualTo(Digest(File.ReadAllBytes(fixture.BatchPath))));
    await fixture.AssertRestores(25);
  }

  [TestCase(false, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(true, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(false, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(true, DiskSegmentMode.MultiPartDiskSegment)]
  public void DamagedDiskFilesFailRestore(bool truncate, DiskSegmentMode mode)
  {
    using var fixture = new BackupFixture(disk: true, mode);
    fixture.Backup();
    var file = fixture.Catalog.Files.OrderByDescending(x => x.ByteLength).First();
    var path = fixture.PathOf(file.BackupPath);
    if (truncate)
    {
      using var stream = File.OpenWrite(path);
      stream.SetLength(stream.Length - 1);
    }
    else FlipLastByte(path);
    var error = Assert.ThrowsAsync<LiveBackupIntegrityException>(async () => await fixture.Restore());
    Assert.That(error.FileName, Is.EqualTo(file.FileName));
  }

  [Test]
  public void SameLengthRecordCorruptionFailsChecksum()
  {
    using var fixture = new BackupFixture();
    fixture.Backup();
    FlipLastByte(fixture.BatchPath); // An int value changes; framing remains valid.
    var error = Assert.ThrowsAsync<LiveBackupIntegrityException>(async () => await fixture.Restore());
    Assert.That(error.Message, Does.Contain("SHA-256"));
  }

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void TruncationAtRecordOrBlockBoundaryFailsRestore(bool compressed, bool legacy)
  {
    using var fixture = new BackupFixture();
    fixture.Backup(compressed ? CompressionMethod.Zstd : CompressionMethod.None);
    if (legacy) fixture.MakeLegacy();
    var path = fixture.BatchPath;
    var bytes = File.ReadAllBytes(path);
    // Fixture blocks hold one 25-byte record. Keep exactly one complete record/block.
    var length = compressed ? 8 + BitConverter.ToInt32(bytes, 4) : 25;
    using (var stream = File.OpenWrite(path)) stream.SetLength(length);
    var error = Assert.ThrowsAsync<LiveBackupIntegrityException>(async () => await fixture.Restore());
    Assert.That(error.Message, Does.Contain("Expected 20 records"));
  }

  [TestCase("count")]
  [TestCase("stored-length")]
  [TestCase("uncompressed-length")]
  [TestCase("missing-checksum")]
  [TestCase("empty-count")]
  [TestCase("index")]
  public void InvalidRecordBatchMetadataFailsRestore(string damage)
  {
    using var fixture = new BackupFixture();
    fixture.Backup();
    var catalog = fixture.Catalog;
    var batch = catalog.RecordBatch;
    switch (damage)
    {
      case "count": ++batch.RecordCount; break;
      case "stored-length": ++batch.StoredLength; break;
      case "uncompressed-length": ++batch.UncompressedLength; break;
      case "missing-checksum": batch.Sha256 = null; break;
      case "empty-count": batch.RecordCount = 0; break;
      case "index":
        var bytes = File.ReadAllBytes(fixture.BatchPath);
        BitConverter.GetBytes(2L).CopyTo(bytes, 0);
        File.WriteAllBytes(fixture.BatchPath, bytes);
        batch.Sha256 = Digest(bytes); // Exercise the semantic check independently of hashing.
        break;
    }
    fixture.SaveCatalog(catalog);
    Assert.ThrowsAsync<LiveBackupIntegrityException>(async () => await fixture.Restore());
  }

  [Test]
  public async Task LegacyBackupStillRestoresWithLengthAndCountValidation()
  {
    using var fixture = new BackupFixture(disk: true);
    fixture.Backup();
    fixture.MakeLegacy();
    await fixture.AssertRestores(25);
  }

  [Test]
  public async Task NewGenerationRecopiesLegacyFilesFromTheSource()
  {
    using var fixture = new BackupFixture(disk: true);
    fixture.Backup();
    fixture.MakeLegacy();
    FlipLastByte(fixture.PathOf(fixture.Catalog.Files.OrderByDescending(x => x.ByteLength).First().BackupPath));
    // A fresh store must not hash damaged legacy backup bytes and call them valid.
    fixture.Backup();
    Assert.That(fixture.Catalog.Version, Is.EqualTo(2));
    Assert.That(fixture.Catalog.Files.All(x => x.Sha256?.Length == 64), Is.True);
    await fixture.AssertRestores(25);
  }

  [Test]
  public void ReusedFilesKeepTheirOriginalChecksumAcrossStoreRestarts()
  {
    using var fixture = new BackupFixture(disk: true);
    fixture.Backup();
    var original = fixture.Catalog.Files.OrderByDescending(x => x.ByteLength).First();
    var path = fixture.PathOf(original.BackupPath);
    FlipLastByte(path);
    var changed = File.ReadAllBytes(path);
    fixture.Backup();
    var reused = fixture.Catalog.Files.Single(x => x.BackupPath == original.BackupPath);
    Assert.That(reused.Sha256, Is.EqualTo(original.Sha256));
    Assert.That(File.ReadAllBytes(path), Is.EqualTo(changed)); // Incremental reuse does not recopy unchanged-size files.
    Assert.ThrowsAsync<LiveBackupIntegrityException>(async () => await fixture.Restore());
  }

  [Test]
  public async Task CoordinatorRestartDoesNotOverwriteOlderRecordBatches()
  {
    using var fixture = new BackupFixture();
    fixture.Backup();
    var first = fixture.Catalog;
    fixture.Tree.Upsert(0, 999);
    fixture.Backup();
    Assert.That(fixture.Catalog.RecordBatch.BackupPath, Is.Not.EqualTo(first.RecordBatch.BackupPath));
    using var restored = await fixture.Factory().RestoreFromLiveBackupGeneration(fixture.BackupPath, first.GenerationId);
    Assert.That(restored.TryGet(0, out var value) && value == 100, Is.True);
  }

  [Test]
  public async Task NonSeekableBatchSourcesAreValidated()
  {
    var batch = new LiveBackupRecordBatch { BatchId = 1, RecordCount = 1, CompressionMethod = CompressionMethod.None };
    using var bytes = new MemoryStream();
    // This stream intentionally keeps the buffer available after writer disposal.
    await using (var writer = new LiveBackupRecordBatchWriter(new NonSeekableStream(bytes, leaveOpen: true), batch))
      await writer.WriteAsync(new LiveBackupRecord { RecordIndex = 1, Key = new byte[] { 1 }, Value = new byte[] { 2 } }, CancellationToken.None);
    var data = bytes.ToArray();
    using (var reader = new LiveBackupRecordBatchReader(new NonSeekableStream(new MemoryStream(data)), batch))
    {
      Assert.That(reader.TryRead(out var record) && record.Value.Span[0] == 2, Is.True);
      Assert.That(reader.TryRead(out _), Is.False);
      Assert.That(reader.TryRead(out _), Is.False);
    }
    data[^1] ^= 1;
    using var corrupt = new LiveBackupRecordBatchReader(new NonSeekableStream(new MemoryStream(data)), batch);
    Assert.That(corrupt.TryRead(out _), Is.True);
    Assert.Throws<LiveBackupIntegrityException>(() => corrupt.TryRead(out _));
  }

  [Test]
  public async Task SynchronousBackupDoesNotCaptureTheCallersContext()
  {
    using var fixture = new BackupFixture();
    var context = new RecordingContext();
    using var backup = fixture.Tree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = new ContextCapturingStore(), IncludeInMemoryRecords = false, BackupAfterMerge = false
    });
    await Task.Run(() =>
    {
      SynchronizationContext.SetSynchronizationContext(context);
      try { backup.CreateGeneration(); backup.WaitForLiveBackup(); }
      finally { SynchronizationContext.SetSynchronizationContext(null); }
    }).WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(context.Posts, Is.Zero);
  }

  sealed class RecordingContext : SynchronizationContext
  {
    public int Posts;
    public override void Post(SendOrPostCallback callback, object state)
    {
      Interlocked.Increment(ref Posts);
      // Run callbacks so an old implementation fails the assertion instead of hanging NUnit.
      ThreadPool.QueueUserWorkItem(_ => callback(state));
    }
  }

  sealed class ContextCapturingStore : ILiveBackupStore
  {
    public async Task<long> GetNextGenerationIdAsync(CancellationToken token) { await Task.Delay(20, token); return 1; }
    public Task BeginGenerationAsync(long id, DateTime utc, LiveBackupTreeKind treeKind, CancellationToken token) => Task.CompletedTask;
    public Task<bool> UseSegmentAsync(long id, ZoneTree.Segments.Disk.DiskSegmentFile file, CancellationToken token) => Task.FromResult(false);
    public Task UploadSegmentFileAsync(long id, ZoneTree.Segments.Disk.DiskSegmentFile file, Stream source, CancellationToken token) => Task.CompletedTask;
    public Task<ILiveBackupRecordWriter> OpenRecordWriterAsync(long id, LiveBackupRecordBatch batch, CancellationToken token) => throw new NotSupportedException();
    public Task CompleteGenerationAsync(long id, long opIndex, CancellationToken token) => Task.CompletedTask;
  }

  sealed class NonSeekableStream(Stream inner, bool leaveOpen = false) : Stream
  {
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer[..Math.Min(buffer.Length, 3)]); // Partial reads.
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing && !leaveOpen) inner.Dispose(); base.Dispose(disposing); }
  }

  static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
  static void FlipLastByte(string path)
  {
    var bytes = File.ReadAllBytes(path);
    bytes[^1] ^= 1;
    File.WriteAllBytes(path, bytes);
  }

  sealed class BackupFixture : IDisposable
  {
    readonly string Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "backup-integrity", Guid.NewGuid().ToString("N"));
    public IZoneTree<int, int> Tree { get; }
    public string BackupPath => Path.Combine(Root, "backup");
    public LocalLiveBackupGenerationCatalog Catalog => new LocalLiveBackupProvider(BackupPath).ReadCurrentGeneration();
    public string BatchPath => PathOf(Catalog.RecordBatch.BackupPath);
    public string PathOf(string relative) => Path.Combine(BackupPath, relative.Replace('/', Path.DirectorySeparatorChar));

    public BackupFixture(bool disk = false, DiskSegmentMode mode = DiskSegmentMode.SingleDiskSegment)
    {
      Tree = new ZoneTreeFactory<int, int>().DisableDeletion()
          .SetDataDirectory(Path.Combine(Root, "source"))
          .Configure(o => o.AllowUnsafeOptionValues = true)
          .SetMutableSegmentMaxItemCount(10000)
          .ConfigureDiskSegmentOptions(o => { o.DiskSegmentMode = mode; o.MinimumRecordCount = 2; o.MaximumRecordCount = 8; })
          .OpenOrCreate();
      for (var i = 0; i < 20; ++i) Tree.Upsert(i, i + 100);
      if (disk)
      {
        Tree.Maintenance.MoveMutableSegmentForward();
        Tree.Maintenance.StartMergeOperation()?.Join();
        for (var i = 20; i < 25; ++i) Tree.Upsert(i, i + 100);
      }
    }
    public void Backup(CompressionMethod codec = CompressionMethod.None)
    {
      using var backup = Tree.CreateLiveBackup(new LiveBackupOptions
      {
        Store = new LocalLiveBackupProvider(BackupPath), BackupAfterMerge = false,
        RecordBatchCompression = new LiveBackupCompressionOptions { Method = codec, BlockSize = 25 }
      });
      backup.CreateGeneration();
    }
    public void SaveCatalog(LocalLiveBackupGenerationCatalog catalog) => File.WriteAllText(
        Path.Combine(BackupPath, "generations", catalog.GenerationId.ToString("D20") + ".json"), JsonSerializer.Serialize(catalog));
    public void MakeLegacy()
    {
      var catalog = Catalog;
      catalog.Version = 1;
      foreach (var file in catalog.Files) file.Sha256 = null;
      if (catalog.RecordBatch != null) catalog.RecordBatch.Sha256 = null;
      SaveCatalog(catalog);
    }
    public ZoneTreeFactory<int, int> Factory() => new ZoneTreeFactory<int, int>().DisableDeletion().SetDataDirectory(Path.Combine(Root, "restore"));
    public async Task Restore() { using var restored = await Factory().RestoreFromLatestLiveBackup(BackupPath); }
    public async Task AssertRestores(int count)
    {
      using var restored = await Factory().RestoreFromLatestLiveBackup(BackupPath);
      Assert.That(restored.CountFullScan(), Is.EqualTo(count));
      for (var i = 0; i < count; ++i) Assert.That(restored.TryGet(i, out var value) && value == i + 100, Is.True);
    }
    public void Dispose() { Tree.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
  }
}
