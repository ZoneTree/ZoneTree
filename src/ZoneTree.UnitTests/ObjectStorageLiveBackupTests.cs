using System.Runtime.CompilerServices;
using System.Text.Json;
using ZoneTree.Backup;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class ObjectStorageLiveBackupTests
{
  [TestCase(false, WriteAheadLogMode.Shared, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(false, WriteAheadLogMode.Shared, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(false, WriteAheadLogMode.AsyncShared, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(false, WriteAheadLogMode.AsyncShared, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(true, WriteAheadLogMode.Shared, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(true, WriteAheadLogMode.Shared, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(true, WriteAheadLogMode.AsyncShared, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(true, WriteAheadLogMode.AsyncShared, DiskSegmentMode.MultiPartDiskSegment)]
  public async Task RoundTripAndIncrementalReuse(bool transactional, WriteAheadLogMode wal, DiskSegmentMode disk)
  {
    using var f = new Fixture(transactional, wal, disk);
    await f.Backup();
    string[] original;
    await using (var first = await f.Provider().ReadLatestGenerationAsync(default))
    {
      Assert.That(first.Files, Is.Not.Empty);
      Assert.That(first.TreeKind, Is.EqualTo(transactional ? LiveBackupTreeKind.Transactional : LiveBackupTreeKind.Plain));
      original = first.Files.Select(x => x.StorageKey).ToArray();
    }
    f.Advance();
    await f.Backup();
    await using (var second = await f.Provider().ReadLatestGenerationAsync(default))
    {
      Assert.That(second.GenerationId, Is.EqualTo(2));
      Assert.That(second.Files.Select(x => x.StorageKey), Is.EquivalentTo(original));
      Assert.That(second.Files.All(x => x.Sha256.Length == 64), Is.True);
      Assert.That(second.RecordBatch.Sha256.Length, Is.EqualTo(64));
    }
    Assert.That(original.All(key => f.Storage.CreateCounts[key] == 1), Is.True, "Reused immutable files must not be transferred again.");
    await f.AssertRestores(1, advanced: false);
    await f.AssertRestores(2, advanced: true);
    Assert.That(await f.Provider().ListRestorePinsAsync(), Is.Empty);
    Assert.That(f.Storage.OpenReads, Is.Zero);
    Assert.That(f.Storage.OpenWrites, Is.Zero);
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task IndependentClientsProtectRestoresAcrossRetention(bool transactional)
  {
    using var f = new Fixture(transactional, keep: 1);
    await f.Backup();
    var reader = f.Provider();
    await using var pinned = await reader.ReadGenerationAsync(1, default);
    f.Advance();
    await f.Backup();
    await f.AssertRestores(1, advanced: false);
    Assert.That(await f.Provider().ListGenerationIdsAsync(), Is.EqualTo(new[] { 2L, 1L }));
    Assert.That((await f.Provider().ListRestorePinsAsync()).Single().GenerationId, Is.EqualTo(1));
    await pinned.DisposeAsync();
    await f.Provider().ApplyRetentionAsync();
    Assert.ThrowsAsync<FileNotFoundException>(() => f.Provider().ReadGenerationAsync(1, default));
    Assert.That(await f.Provider().ListGenerationIdsAsync(), Is.EqualTo(new[] { 2L }));
    await f.AssertRestores(2, advanced: true);
    Assert.That(f.Storage.Keys.Any(x => x.Contains("00000000000000000001/records.bin", StringComparison.Ordinal)), Is.False);
    Assert.That(f.Storage.Keys.Any(x => x.Contains("00000000000000000001/segments/", StringComparison.Ordinal)), Is.True,
        "Files reused by the head remain protected after their original generation is retired.");
  }

  [Test]
  public async Task AbandonedPinsRequireExplicitRelease()
  {
    using var f = new Fixture(true, keep: 1);
    await f.Backup();
    var abandoned = await f.Provider().ReadGenerationAsync(1, default);
    f.Advance(); await f.Backup();
    var pin = (await f.Provider().ListRestorePinsAsync()).Single();
    Assert.That(pin.CreatedAtUnixSeconds, Is.GreaterThan(0));
    await f.Provider().ReleaseRestorePinAsync(pin.Id);
    await f.Provider().ApplyRetentionAsync();
    Assert.ThrowsAsync<FileNotFoundException>(() => f.Provider().ReadGenerationAsync(1, default));
    await abandoned.DisposeAsync(); // Releasing an already removed pin is idempotent.
    await f.AssertRestores(2, true);
  }

  [TestCase("manifest")]
  [TestCase("catalog")]
  [TestCase("segment")]
  [TestCase("batch")]
  public async Task CorruptionFailsClosedAndReleasesPins(string part)
  {
    using var f = new Fixture(true);
    f.Advance();
    await f.Backup();
    string key;
    await using (var generation = await f.Provider().ReadLatestGenerationAsync(default))
      key = part switch
      {
        "manifest" => f.Storage.Keys.Single(x => x.EndsWith("manifest.ztb", StringComparison.Ordinal)),
        "catalog" => f.Storage.Keys.Single(x => x.EndsWith("catalog.json", StringComparison.Ordinal)),
        "segment" => generation.Files[0].StorageKey,
        _ => generation.RecordBatch.StorageKey
      };
    f.Storage.Corrupt(key);
    var deletes = f.Storage.Deletes;
    Assert.ThrowsAsync<LiveBackupIntegrityException>(() => f.AssertRestores(1, false));
    if (part is "manifest" or "catalog")
    {
      Assert.ThrowsAsync<LiveBackupIntegrityException>(() => f.Provider().ApplyRetentionAsync());
      Assert.That(f.Storage.Deletes, Is.EqualTo(deletes));
    }
    if (part != "manifest") Assert.That(await f.Provider().ListRestorePinsAsync(), Is.Empty);
    Assert.That(f.Storage.OpenReads, Is.Zero);
  }

  [TestCase("records.bin", false)]
  [TestCase("records.bin", true)]
  [TestCase("catalog.json", false)]
  [TestCase("catalog.json", true)]
  [TestCase("publish", false)]
  [TestCase("publish", true)]
  public async Task InterruptedAndUncertainCommitsNeverOverwriteAcknowledgedGenerations(string stage, bool after)
  {
    using var f = new Fixture(true, keep: 1);
    await f.Backup(); f.Advance();
    var failed = false;
    Task Fail(string operation, string key, byte[] bytes)
    {
      var match = stage == "publish" ? operation == "cas" && State(bytes).CurrentGenerationId == 2 && State(bytes).ActiveGenerationId == 0 :
          operation == "create" && key.Contains("00000000000000000002/", StringComparison.Ordinal) && key.EndsWith(stage, StringComparison.Ordinal);
      if (!failed && match) { failed = true; throw new IOException("Injected uncertain object operation."); }
      return Task.CompletedTask;
    }
    if (after) f.Storage.AfterMutation = Fail; else f.Storage.BeforeMutation = Fail;
    Assert.ThrowsAsync<LiveBackupGenerationException>(() => f.Backup());
    Assert.That(failed, Is.True);
    f.Storage.BeforeMutation = f.Storage.AfterMutation = null;
    if (after && stage == "publish") await f.AssertRestores(2, true);
    else await f.AssertRestores(1, false);
    await f.Backup(); // Allocation state survives an interrupted catalog/manifest publication.
    await using (var latest = await f.Provider().ReadLatestGenerationAsync(default)) Assert.That(latest.GenerationId, Is.EqualTo(3));
    await f.AssertRestores(3, true);
    Assert.That(f.Storage.Keys.Any(x => x.Contains("00000000000000000002/", StringComparison.Ordinal)), Is.False);
    Assert.That(f.Storage.OpenWrites, Is.Zero);
  }

  [Test]
  public async Task ANewWriterFencesAnUnfinishedWriter()
  {
    using var f = new Fixture(true);
    var entered = Signal(); var release = Signal();
    f.Storage.BeforeMutation = async (operation, key, _) =>
    {
      if (operation == "create" && key.Contains("00000000000000000001/records.bin", StringComparison.Ordinal))
      { entered.TrySetResult(); await release.Task; }
    };
    var oldAttempt = f.Backup();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try { await f.Backup().WaitAsync(TimeSpan.FromSeconds(10)); }
    finally { release.TrySetResult(); }
    Assert.ThrowsAsync<LiveBackupGenerationException>(async () => await oldAttempt);
    await f.Provider().ApplyRetentionAsync();
    await f.AssertRestores(2, false);
    Assert.That(f.Storage.Keys.Any(x => x.Contains("00000000000000000001/", StringComparison.Ordinal)), Is.False);
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task OldGarbageCollectionCannotDeleteNewOrPreviouslyActiveUploads(bool allocateBeforeCapture)
  {
    using var f = new Fixture(true, keep: 1);
    await f.Backup(); f.Advance();
    var uploadEntered = Signal(); var uploadRelease = Signal();
    var listEntered = Signal(); var listRelease = Signal();
    var lists = 0;
    f.Storage.BeforeMutation = async (operation, key, _) =>
    {
      if (allocateBeforeCapture && operation == "create" && key.Contains("00000000000000000002/records.bin", StringComparison.Ordinal))
      { uploadEntered.TrySetResult(); await uploadRelease.Task; }
    };
    Task writing = null;
    if (allocateBeforeCapture)
    { writing = f.Backup(); await uploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
    f.Storage.BeforeList = async () =>
    { if (Interlocked.Increment(ref lists) == 1) { listEntered.TrySetResult(); await listRelease.Task; } };
    var collecting = f.Provider().ApplyRetentionAsync();
    await listEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try
    {
      if (allocateBeforeCapture) uploadRelease.TrySetResult(); else writing = f.Backup();
      await writing.WaitAsync(TimeSpan.FromSeconds(10));
    }
    finally { uploadRelease.TrySetResult(); listRelease.TrySetResult(); }
    await collecting.WaitAsync(TimeSpan.FromSeconds(10));
    await f.AssertRestores(2, true);
  }

  [Test]
  public async Task PinRacingWithRetirementEitherWinsOrFailsBeforeReadingFiles()
  {
    using var f = new Fixture(true, keep: 1);
    await f.Backup(); f.Advance();
    var entered = Signal(); var release = Signal();
    var hits = 0;
    f.Storage.BeforeMutation = async (operation, _, bytes) =>
    {
      if (operation == "cas" && State(bytes).RestorePins.Count != 0 && Interlocked.Increment(ref hits) == 1)
      { entered.TrySetResult(); await release.Task; }
    };
    var pinning = f.Provider().ReadGenerationAsync(1, default);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try { await f.Backup().WaitAsync(TimeSpan.FromSeconds(10)); }
    finally { release.TrySetResult(); }
    Assert.ThrowsAsync<FileNotFoundException>(async () => await pinning);
    Assert.That(await f.Provider().ListRestorePinsAsync(), Is.Empty);
    await f.AssertRestores(2, true);
  }

  [Test]
  public async Task ContentDerivedEtagsCannotRepeatAfterPinRoundTrip()
  {
    using var f = new Fixture(true);
    f.Storage.ContentDerivedVersions = true;
    await f.Backup();
    var before = await f.Storage.GetInfoAsync("test/manifest.ztb", default);
    await using (var pin = await f.Provider().ReadLatestGenerationAsync(default))
      Assert.That(await f.Provider().ListRestorePinsAsync(), Has.Count.EqualTo(1));
    var after = await f.Storage.GetInfoAsync("test/manifest.ztb", default);
    Assert.That(after.Version, Is.Not.EqualTo(before.Version));
    Assert.That(await f.Provider().ListRestorePinsAsync(), Is.Empty);
    await f.AssertRestores(1, false);
  }

  [Test]
  public async Task LostPinAcknowledgmentLeavesAnInspectableProtectionPin()
  {
    using var f = new Fixture(true); await f.Backup();
    var failed = false;
    f.Storage.AfterMutation = (operation, _, bytes) =>
    {
      if (!failed && operation == "cas" && State(bytes).RestorePins.Count != 0)
      { failed = true; throw new IOException("Pin committed but acknowledgment was lost."); }
      return Task.CompletedTask;
    };
    Assert.ThrowsAsync<IOException>(() => f.Provider().ReadLatestGenerationAsync(default));
    f.Storage.AfterMutation = null;
    var pin = (await f.Provider().ListRestorePinsAsync()).Single();
    await f.Provider().ReleaseRestorePinAsync(pin.Id);
    Assert.That(await f.Provider().ListRestorePinsAsync(), Is.Empty);
    Assert.That(f.Storage.OpenReads, Is.Zero);
  }

  [Test]
  public async Task CancellationAfterPinAcquisitionReleasesProtection()
  {
    using var f = new Fixture(true); await f.Backup();
    using var cancellation = new CancellationTokenSource();
    f.Storage.BeforeRead = key =>
    {
      if (key.EndsWith("catalog.json", StringComparison.Ordinal)) cancellation.Cancel();
      return Task.CompletedTask;
    };
    Assert.ThrowsAsync<OperationCanceledException>(() => f.Provider().ReadLatestGenerationAsync(cancellation.Token));
    f.Storage.BeforeRead = null;
    Assert.That(await f.Provider().ListRestorePinsAsync(), Is.Empty);
    Assert.That(f.Storage.OpenReads, Is.Zero);
  }

  [Test]
  public async Task IncompleteRecordWriterCannotBePublished()
  {
    var storage = new MemoryStorage();
    var provider = new ObjectStorageLiveBackupProvider(storage);
    var id = await provider.GetNextGenerationIdAsync(default);
    await provider.BeginGenerationAsync(id, DateTime.UtcNow, LiveBackupTreeKind.Plain, default);
    var batch = new LiveBackupRecordBatch { BatchId = id };
    await using var writer = await provider.OpenRecordWriterAsync(id, batch, default);
    Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteGenerationAsync(id, 0, default));
    await writer.DisposeAsync();
    await provider.CompleteGenerationAsync(id, 0, default);
    await using var generation = await provider.ReadLatestGenerationAsync(default);
    Assert.That(generation.RecordBatch.RecordCount, Is.Zero);
  }

  [Test]
  public async Task CollectorRetriesWhenAnotherCollectorRetiresItsCatalogSnapshot()
  {
    using var f = new Fixture(true, keep: 1);
    await f.Backup(); f.Advance();
    var entered = Signal(); var release = Signal(); var hits = 0;
    f.Storage.BeforeRead = async key =>
    {
      if (key.EndsWith("00000000000000000001/catalog.json", StringComparison.Ordinal) && Interlocked.Increment(ref hits) == 1)
      { entered.TrySetResult(); await release.Task; }
    };
    var collecting = f.Provider().ApplyRetentionAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try { await f.Backup().WaitAsync(TimeSpan.FromSeconds(10)); }
    finally { release.TrySetResult(); }
    await collecting.WaitAsync(TimeSpan.FromSeconds(10));
    await f.AssertRestores(2, true);
  }

  [Test]
  public async Task MissingRetainedCatalogStopsDeletion()
  {
    using var f = new Fixture(true);
    await f.Backup();
    var key = f.Storage.Keys.Single(x => x.EndsWith("catalog.json", StringComparison.Ordinal));
    await f.Storage.DeleteAsync(key, default);
    var deletes = f.Storage.Deletes;
    Assert.ThrowsAsync<FileNotFoundException>(() => f.Provider().ApplyRetentionAsync());
    Assert.That(f.Storage.Deletes, Is.EqualTo(deletes));
  }

  [Test]
  public async Task ConcurrentClientsKeepEveryPinnedRestoreReadable()
  {
    using var f = new Fixture(true, keep: 2);
    f.Advance(); await f.Backup();
    var writing = Task.Run(async () => { for (var i = 0; i < 12; ++i) { await f.Backup(); await Task.Delay(1); } });
    var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
    {
      for (var i = 0; i < 6; ++i)
      {
        await using var pin = await f.Provider().ReadLatestGenerationAsync(default);
        await Task.Delay(1);
        await f.AssertRestores(pin.GenerationId, true);
      }
    }));
    var collecting = Task.Run(async () => { for (var i = 0; i < 20; ++i) { await f.Provider().ApplyRetentionAsync(); await Task.Delay(1); } });
    await Task.WhenAll(readers.Append(writing).Append(collecting)).WaitAsync(TimeSpan.FromSeconds(30));
    Assert.That(await f.Provider().ListRestorePinsAsync(), Is.Empty);
    await f.Provider().ApplyRetentionAsync();
    await f.AssertRestores(13, true);
  }

  [Test]
  public async Task ConcurrentAllocationsAreUniqueAndMonotonic()
  {
    var storage = new MemoryStorage();
    var ids = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
        new ObjectStorageLiveBackupProvider(storage).GetNextGenerationIdAsync(default))));
    Assert.That(ids.Order(), Is.EqualTo(Enumerable.Range(1, 24).Select(x => (long)x)));
  }

  [Test]
  public async Task MissingManifestNeverReinitializesOverExistingGenerations()
  {
    using var f = new Fixture(false);
    await f.Backup();
    await f.Storage.DeleteAsync("test/manifest.ztb", default);
    var before = f.Storage.Keys;
    Assert.ThrowsAsync<LiveBackupIntegrityException>(() => f.Provider().GetNextGenerationIdAsync(default));
    Assert.That(f.Storage.Keys, Is.EqualTo(before));
  }

  [Test]
  public async Task WrongRestoreKindAndCancelledReadsDoNotLeakPins()
  {
    using var f = new Fixture(true); await f.Backup();
    Assert.ThrowsAsync<LiveBackupIntegrityException>(async () =>
    { using var wrong = await f.Factory("wrong").RestoreFromLatestLiveBackup(f.Provider()); });
    Assert.That(await f.Provider().ListRestorePinsAsync(), Is.Empty);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    Assert.ThrowsAsync<OperationCanceledException>(() => f.Provider().ReadLatestGenerationAsync(cancelled.Token));
    Assert.That(await f.Provider().ListRestorePinsAsync(), Is.Empty);
  }

  static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
  static ObjectBackupState State(byte[] bytes) => JsonSerializer.Deserialize(bytes.AsSpan(40), ObjectBackupJsonContext.Default.ObjectBackupState);

  sealed class Fixture : IDisposable
  {
    readonly string Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, nameof(ObjectStorageLiveBackupTests), Guid.NewGuid().ToString("N"));
    readonly bool Transactional;
    readonly WriteAheadLogMode Wal;
    readonly DiskSegmentMode Disk;
    readonly int? Keep;
    readonly IZoneTree<int, int> Plain;
    readonly ITransactionalZoneTree<int, int> Transactions;
    public MemoryStorage Storage { get; } = new();
    public Fixture(bool transactional, WriteAheadLogMode wal = WriteAheadLogMode.AsyncShared, DiskSegmentMode disk = DiskSegmentMode.SingleDiskSegment, int? keep = null)
    {
      Transactional = transactional; Wal = wal; Disk = disk; Keep = keep;
      if (transactional) Transactions = Factory("source").CreateTransactional(); else Plain = Factory("source").Create();
      for (var i = 0; i < 12; ++i) Write(i, i + 100);
      if (Plain != null) { Plain.Maintenance.MoveMutableSegmentForward(); Plain.Maintenance.StartMergeOperation()?.Join(); }
      else { var m = Transactions.Maintenance.ZoneTree.Maintenance; m.MoveMutableSegmentForward(); m.StartMergeOperation()?.Join(); }
    }
    public ZoneTreeFactory<int, int> Factory(string path) => new ZoneTreeFactory<int, int>().SetDataDirectory(Path.Combine(Root, path))
        .Configure(o => o.AllowUnsafeOptionValues = true).SetMutableSegmentMaxItemCount(1000)
        .ConfigureDiskSegmentOptions(o => { o.DiskSegmentMode = Disk; o.MinimumRecordCount = 2; o.MaximumRecordCount = 4; })
        .ConfigureWriteAheadLogOptions(o => { o.WriteAheadLogMode = Wal; o.CompressionBlockSize = 1024; });
    public ObjectStorageLiveBackupProvider Provider() => new(Storage, new ObjectStorageLiveBackupOptions { Prefix = "test", KeepLastGenerations = Keep });
    void Write(int key, int value)
    {
      if (Plain != null) Plain.Upsert(key, value);
      else { using var tx = Transactions.BeginTransaction(); tx.Upsert(key, value); tx.Commit(); }
    }
    public void Advance()
    {
      Write(0, 2000); Write(50, 5000);
      if (Plain != null) Plain.ForceDelete(2);
      else { using var tx = Transactions.BeginTransaction(); tx.Delete(2); tx.Commit(); }
    }
    public async Task Backup()
    {
      var options = new LiveBackupOptions { Store = Provider(), BackupAfterMerge = false, InMemoryMode = LiveBackupInMemoryMode.Snapshot };
      using ILiveBackup backup = Transactional ? Transactions.CreateLiveBackup(options) : Plain.CreateLiveBackup(options);
      await backup.CreateGenerationAsync();
    }
    public async Task AssertRestores(long id, bool advanced)
    {
      var path = "restored-" + Guid.NewGuid().ToString("N");
      if (Transactional)
      {
        using var restored = await Factory(path).RestoreTransactionalFromLiveBackupGeneration(Provider(), id);
        using var read = restored.BeginReadOnlyTransaction();
        Assert.That(read.Range(int.MinValue, int.MaxValue).Count(), Is.EqualTo(12));
        for (var key = 0; key < 12; ++key)
        {
          if (advanced && key == 2) { Assert.That(read.ContainsKey(key), Is.False); continue; }
          Assert.That(read.TryGet(key, out var value) && value == (advanced && key == 0 ? 2000 : key + 100), Is.True, $"key {key}");
        }
        Assert.That(read.ContainsKey(50), Is.EqualTo(advanced));
      }
      else
      {
        using var restored = await Factory(path).RestoreFromLiveBackupGeneration(Provider(), id);
        Assert.That(restored.CountFullScan(), Is.EqualTo(12));
        for (var key = 0; key < 12; ++key)
        {
          if (advanced && key == 2) { Assert.That(restored.ContainsKey(key), Is.False); continue; }
          Assert.That(restored.TryGet(key, out var value) && value == (advanced && key == 0 ? 2000 : key + 100), Is.True, $"key {key}");
        }
        Assert.That(restored.ContainsKey(50), Is.EqualTo(advanced));
      }
    }
    public void Dispose() { Plain?.Dispose(); Transactions?.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
  }

  internal sealed class MemoryStorage : IBackupObjectStorage
  {
    readonly object Gate = new();
    readonly Dictionary<string, (byte[] Bytes, string Version)> Objects = new(StringComparer.Ordinal);
    long NextVersion;
    public readonly Dictionary<string, int> CreateCounts = new(StringComparer.Ordinal);
    public Func<string, string, byte[], Task> BeforeMutation, AfterMutation;
    public Func<Task> BeforeList;
    public Func<string, Task> BeforeRead;
    public Func<string, Task> BeforeDelete;
    public int OpenReads, OpenWrites, Deletes;
    public bool ContentDerivedVersions;
    string VersionFor(byte[] bytes) => ContentDerivedVersions
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) : (++NextVersion).ToString();
    public string[] Keys { get { lock (Gate) return Objects.Keys.Order(StringComparer.Ordinal).ToArray(); } }
    public void Corrupt(string key)
    { lock (Gate) { var bytes = Objects[key].Bytes.ToArray(); bytes[^1] ^= 1; Objects[key] = (bytes, VersionFor(bytes)); } }
    public async ValueTask<BackupObjectRead> TryOpenReadAsync(string key, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (BeforeRead != null) await BeforeRead(key);
      lock (Gate)
      {
        if (!Objects.TryGetValue(key, out var entry)) return null;
        Interlocked.Increment(ref OpenReads);
        return new ReadHandle(this, new(key, entry.Bytes.Length, entry.Version), entry.Bytes);
      }
    }
    public ValueTask<BackupObjectInfo> GetInfoAsync(string key, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      lock (Gate) return ValueTask.FromResult(Objects.TryGetValue(key, out var entry) ? new BackupObjectInfo(key, entry.Bytes.Length, entry.Version) : null);
    }
    public ValueTask<BackupObjectWrite> CreateAsync(string key, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref OpenWrites); return ValueTask.FromResult<BackupObjectWrite>(new WriteHandle(this, key)); }
    public async ValueTask<BackupObjectInfo> CompareExchangeAsync(string key, ReadOnlyMemory<byte> content, string expectedVersion, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var bytes = content.ToArray();
      if (BeforeMutation != null) await BeforeMutation("cas", key, bytes);
      BackupObjectInfo info;
      lock (Gate)
      {
        Objects.TryGetValue(key, out var previous);
        if (previous.Version != expectedVersion) return null;
        var version = VersionFor(bytes); Objects[key] = (bytes, version); info = new(key, bytes.Length, version);
      }
      if (AfterMutation != null) await AfterMutation("cas", key, bytes);
      return info;
    }
    public async IAsyncEnumerable<BackupObjectInfo> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
      if (BeforeList != null) await BeforeList();
      BackupObjectInfo[] snapshot;
      lock (Gate) snapshot = Objects.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(x => new BackupObjectInfo(x.Key, x.Value.Bytes.Length, x.Value.Version)).ToArray();
      foreach (var info in snapshot) { cancellationToken.ThrowIfCancellationRequested(); yield return info; }
    }
    public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); if (BeforeDelete != null) await BeforeDelete(key); lock (Gate) { Objects.Remove(key); ++Deletes; } }
    async Task<BackupObjectInfo> Commit(string key, byte[] bytes, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (BeforeMutation != null) await BeforeMutation("create", key, bytes);
      BackupObjectInfo info;
      lock (Gate)
      {
        if (Objects.ContainsKey(key)) throw new IOException("Object already exists.");
        var version = VersionFor(bytes); Objects.Add(key, (bytes, version)); info = new(key, bytes.Length, version);
        CreateCounts[key] = CreateCounts.GetValueOrDefault(key) + 1;
      }
      if (AfterMutation != null) await AfterMutation("create", key, bytes);
      return info;
    }
    sealed class WriteHandle(MemoryStorage store, string key) : BackupObjectWrite
    {
      readonly MemoryStream Buffer = new();
      int Disposed;
      public override Stream Stream => Buffer;
      public override ValueTask<BackupObjectInfo> CommitAsync(CancellationToken cancellationToken) => new(store.Commit(key, Buffer.ToArray(), cancellationToken));
      public override ValueTask DisposeAsync()
      { if (Interlocked.Exchange(ref Disposed, 1) == 0) { Buffer.Dispose(); Interlocked.Decrement(ref store.OpenWrites); } return ValueTask.CompletedTask; }
    }
    sealed class ReadHandle(MemoryStorage store, BackupObjectInfo info, byte[] bytes) : BackupObjectRead
    {
      readonly Stream Content = new ChunkedStream(bytes);
      int Disposed;
      public override BackupObjectInfo Info => info;
      public override Stream Stream => Content;
      public override ValueTask DisposeAsync()
      { if (Interlocked.Exchange(ref Disposed, 1) == 0) { Content.Dispose(); Interlocked.Decrement(ref store.OpenReads); } return ValueTask.CompletedTask; }
    }
    sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
      public override bool CanSeek => false;
      public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 13));
      public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 13)]);
      public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
      { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
    }
  }
}
