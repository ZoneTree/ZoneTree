using ZoneTree.AbstractFileStream;
using ZoneTree.Backup;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class SharedWriteAheadLogTests
{
  static WriteAheadLogOptions Options(WriteAheadLogMode mode, CompressionMethod method = CompressionMethod.None) => new()
  {
    WriteAheadLogMode = mode,
    CompressionBlockSize = 256,
    CompressionMethod = method,
    SharedModeOptions = new() { MaximumChunkSize = 1024 },
    SyncCompressedModeOptions = new() { EnableTailWriterJob = false },
    AsyncCompressedModeOptions = new() { EmptyQueuePollInterval = 10000 }
  };

  static WriteAheadLogProvider Provider(IFileStreamProvider files) => new(SilentTestLogger.Instance, files, "data");

  static IWriteAheadLog<int, int> Open(WriteAheadLogProvider provider, long id, WriteAheadLogOptions options)
      => provider.GetOrCreateWAL(id, "seg", options, new Int32Serializer(), new Int32Serializer());

  static ZoneTreeFactory<int, int> Factory(IFileStreamProvider files, WriteAheadLogMode mode)
      => new ZoneTreeFactory<int, int>(files).SetDataDirectory("data").SetLogger(SilentTestLogger.Instance)
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .SetMutableSegmentMaxItemCount(32)
          .ConfigureWriteAheadLogOptions(wal =>
          {
            wal.WriteAheadLogMode = mode;
            wal.CompressionBlockSize = 256;
            wal.SharedModeOptions.MaximumChunkSize = 1024;
          });

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task BusyWriterClosesCompletedChunksWithoutSyncingThem(WriteAheadLogMode mode)
  {
    const int count = 8192;
    using var start = new ManualResetEventSlim(mode != WriteAheadLogMode.AsyncShared);
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var files = new PowerLossFileStreamProvider();
    var serializer = new PausingKeySerializer(start, entered, release);
    using var wal = Provider(files).GetOrCreateWAL(1, "seg", Options(mode), serializer, new Int32Serializer());
    var initialSyncs = files.DiskSyncCount;
    var producer = Task.Run(() =>
    {
      for (var key = 0; key < count; ++key) wal.Append(key, key + 1, key);
      wal.Append(-1, -1, count);
    });
    try
    {
      if (mode == WriteAheadLogMode.AsyncShared)
      {
        // Hold the first serialization until every item is queued. The final
        // serialization then pauses before the queue can become idle and sync.
        await producer.WaitAsync(TimeSpan.FromSeconds(10));
        start.Set();
      }
      Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
      Assert.That(files.GetFiles("data/seg/shared").Count(path => path.EndsWith(".wal", StringComparison.Ordinal)),
          Is.GreaterThan(100), "exercise many physical chunk rotations");
      Assert.That(files.OpenStreamCount, Is.EqualTo(2), "only the active main and tail stay open");
      Assert.That(files.PeakOpenStreamCount, Is.EqualTo(2), "rotation closes the old chunk before opening another");
      Assert.That(files.DiskSyncCount, Is.EqualTo(initialSyncs), "resource release must not force disk syncs");
    }
    finally
    {
      start.Set();
      release.Set();
      await producer.WaitAsync(TimeSpan.FromSeconds(10));
    }
    await wal.SyncAsync();
    using var recovered = Open(Provider(files.SimulatePowerLoss()), 1, Options(mode));
    Assert.That(recovered.ReadLogEntries(true, true, true).Keys,
        Is.EqualTo(Enumerable.Range(0, count).Append(-1)));
  }

  sealed class PausingKeySerializer(ManualResetEventSlim start, ManualResetEventSlim entered,
      ManualResetEventSlim release) : ISerializer<int>
  {
    public int Deserialize(Memory<byte> bytes) => BitConverter.ToInt32(bytes.Span);

    public Memory<byte> Serialize(in int entry)
    {
      if (entry == 0 && !start.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
      if (entry == -1)
      {
        entered.Set();
        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
      }
      return BitConverter.GetBytes(entry);
    }
  }

  [Test]
  public async Task PhysicalRotationContinuesDuringSyncAndLaterSyncCoversTheSeal()
  {
    const int count = 60;
    var files = new PowerLossFileStreamProvider();
    var options = Options(WriteAheadLogMode.Shared);
    // Rotate before filling a native compression block, so this exercises the
    // handle handoff rather than waiting to write another block to the syncing file.
    options.SharedModeOptions.MaximumChunkSize = 160;
    using var wal = Open(Provider(files), 1, options);
    wal.Append(0, 1, 0);
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var blocked = 0;
    files.BeforeDiskSync = path =>
    {
      if (!path.EndsWith(".wal", StringComparison.Ordinal) ||
          Interlocked.CompareExchange(ref blocked, 1, 0) != 0) return;
      entered.Set();
      if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
    };
    var sync = Task.Run(async () => await wal.SyncAsync());
    Task append = Task.CompletedTask;
    try
    {
      Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
      append = Task.Run(() =>
      {
        for (var key = 1; key < count; ++key) wal.Append(key, key + 1, key);
      });
      await append.WaitAsync(TimeSpan.FromSeconds(10));
      Assert.That(files.PeakOpenStreamCount, Is.LessThanOrEqualTo(4),
          "only the active chunk and the chunk being synced can retain stream pairs");
    }
    finally
    {
      release.Set();
      try { await Task.WhenAll(sync, append).WaitAsync(TimeSpan.FromSeconds(10)); }
      finally { files.BeforeDiskSync = null; }
    }
    await wal.SyncAsync();
    using var recovered = Open(Provider(files.SimulatePowerLoss()), 1, options);
    Assert.That(recovered.ReadLogEntries(true, true, true).Keys, Is.EqualTo(Enumerable.Range(0, count)));
  }

  [TestCase(WriteAheadLogMode.Shared, CompressionMethod.None)]
  [TestCase(WriteAheadLogMode.Shared, CompressionMethod.LZ4)]
  [TestCase(WriteAheadLogMode.Shared, CompressionMethod.Zstd)]
  [TestCase(WriteAheadLogMode.AsyncShared, CompressionMethod.None)]
  [TestCase(WriteAheadLogMode.AsyncShared, CompressionMethod.LZ4)]
  [TestCase(WriteAheadLogMode.AsyncShared, CompressionMethod.Zstd)]
  public async Task SyncProtectsAllHandlesAndDefaultOptionsDetectExistingJournal(WriteAheadLogMode mode, CompressionMethod method)
  {
    var files = new PowerLossFileStreamProvider();
    var provider = Provider(files);
    using var first = Open(provider, 1, Options(mode, method));
    using var second = Open(provider, 2, Options(mode, method));
    for (var key = 0; key < 100; ++key)
    {
      first.Append(key, key + 1, key);
      second.Append(key, key + 2, key);
    }
    await second.SyncAsync();
    var recovered = Provider(files.SimulatePowerLoss());
    using var firstRecovery = Open(recovered, 1, new());
    using var secondRecovery = Open(recovered, 2, new());
    Assert.That(firstRecovery.ReadLogEntries(true, true, true).Values, Is.EqualTo(Enumerable.Range(1, 100)));
    Assert.That(secondRecovery.ReadLogEntries(true, true, true).Values, Is.EqualTo(Enumerable.Range(2, 100)));
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task EachCommitUsesOneMainSyncAndFreezingDoesNotSync(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    var provider = Provider(files);
    var options = Options(mode);
    options.SharedModeOptions.MaximumChunkSize = 1024 * 1024;
    using var first = Open(provider, 1, options);
    using var second = Open(provider, 2, options);
    var main = 0;
    var tail = 0;
    files.BeforeDiskSync = path =>
    {
      if (path.EndsWith(".wal", StringComparison.Ordinal)) Interlocked.Increment(ref main);
      if (path.EndsWith(".tail", StringComparison.Ordinal)) Interlocked.Increment(ref tail);
    };
    for (var key = 0; key < 10; ++key)
    {
      first.Append(key, key, key);
      second.Append(key, key, key);
      await second.SyncAsync();
      Assert.That(main, Is.EqualTo(key + 1));
      Assert.That(tail, Is.Zero);
    }
    first.MarkFrozen();
    Assert.That(main, Is.EqualTo(10));
    first.Dispose();
    second.Append(10, 10, 10);
    await second.SyncAsync();
    Assert.That(main, Is.EqualTo(11));
    files.BeforeDiskSync = null;
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task RotationMergeAndReclamationKeepAllSyncedWrites(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    using var tree = Factory(files, mode).Create();
    for (var key = 0; key < 240; ++key) tree.Upsert(key, key + 1);
    await tree.SyncWALAsync();
    var before = files.GetFiles("data/seg/shared").Count(path => path.EndsWith(".wal", StringComparison.Ordinal));
    tree.Maintenance.MoveMutableSegmentForward();
    tree.Maintenance.StartMergeOperation().Join();
    var after = files.GetFiles("data/seg/shared").Count(path => path.EndsWith(".wal", StringComparison.Ordinal));
    Assert.That(before, Is.GreaterThan(1));
    Assert.That(after, Is.LessThan(before));
    using var recovered = Factory(files.SimulatePowerLoss(), mode).Open();
    for (var key = 0; key < 240; ++key)
    {
      Assert.That(recovered.TryGet(key, out var value), Is.True, $"key {key}");
      Assert.That(value, Is.EqualTo(key + 1));
    }
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task ReplacementIsDurableAndDoesNotReplaceOtherHandles(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    var provider = Provider(files);
    using var first = Open(provider, 1, Options(mode));
    using var second = Open(provider, 2, Options(mode));
    for (var key = 0; key < 100; ++key) first.Append(key, key, key);
    second.Append(3, 4, 300);
    await second.SyncAsync();
    first.ReplaceWriteAheadLog([1, 2], [10, 20]);
    var recovered = Provider(files.SimulatePowerLoss());
    using var firstRecovery = Open(recovered, 1, Options(mode));
    using var secondRecovery = Open(recovered, 2, Options(mode));
    var read = firstRecovery.ReadLogEntries(true, true, true);
    Assert.That(read.Values, Is.EqualTo(new[] { 10, 20 }));
    Assert.That(read.MaximumOpIndex, Is.EqualTo(99));
    Assert.That(secondRecovery.ReadLogEntries(true, true, true).Values, Is.EqualTo(new[] { 4 }));
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task FailedSyncFailsSubsequentSyncs(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    var provider = Provider(files);
    var wal = Open(provider, 1, Options(mode));
    wal.Append(1, 2, 1);
    files.FailDiskSync = path => path.EndsWith(".wal", StringComparison.Ordinal);
    Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(async () => await wal.SyncAsync());
    files.FailDiskSync = null;
    Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(async () => await wal.SyncAsync());
    Assert.Throws<WriteAheadLogSyncFailedException>(wal.Dispose);
    await Task.CompletedTask;
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task TornUnsyncedChunksRecoverOnePrefixAcrossHandles(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    var provider = Provider(files);
    using var first = Open(provider, 1, Options(mode));
    using var second = Open(provider, 2, Options(mode));
    for (var key = 0; key < 20; ++key)
      (key % 2 == 0 ? first : second).Append(key, key, key);
    await first.SyncAsync();
    for (var key = 20; key < 150; ++key)
      (key % 2 == 0 ? first : second).Append(key, key, key);
    // A read drains the async queue without requesting a disk sync.
    second.ReadLogEntries(true, true, true);
    for (var seed = 0; seed < 30; ++seed)
    {
      var recovered = Provider(files.SimulateTornPowerLoss(new Random(seed)));
      using var a = Open(recovered, 1, Options(mode));
      using var b = Open(recovered, 2, Options(mode));
      var keys = a.ReadLogEntries(true, true, true).Keys.Concat(b.ReadLogEntries(true, true, true).Keys).Order().ToArray();
      Assert.That(keys.Length, Is.GreaterThanOrEqualTo(20), $"seed {seed}");
      Assert.That(keys, Is.EqualTo(Enumerable.Range(0, keys.Length)), $"seed {seed}");
    }
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task CrashDuringReplacementKeepsAnEntireOldOrNewSnapshot(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    var provider = Provider(files);
    using var wal = Open(provider, 1, Options(mode));
    for (var key = 0; key < 45; ++key) wal.Append(key, key, key);
    await wal.SyncAsync();
    var images = new List<PowerLossFileStreamProvider>();
    var random = new Random(73);
    void Capture()
    {
      images.Add(files.SimulateTornPowerLoss(random));
    }
    files.AfterWrite = _ => Capture();
    files.BeforeDiskSync = _ => Capture();
    try { wal.ReplaceWriteAheadLog([1, 2], [100, 200]); }
    finally { files.AfterWrite = null; files.BeforeDiskSync = null; }
    Assert.That(images, Is.Not.Empty);
    foreach (var image in images)
    {
      using var recovered = Open(Provider(image), 1, Options(mode));
      var values = recovered.ReadLogEntries(true, true, true).Values;
      Assert.That(values.SequenceEqual(Enumerable.Range(0, 45)) || values.SequenceEqual(new[] { 100, 200 }),
          Is.True, "a replacement snapshot must be published atomically");
      recovered.Append(999, 1000, 1000);
      await recovered.SyncAsync();
      using var again = Open(Provider(image.SimulatePowerLoss()), 1, Options(mode));
      Assert.That(again.ReadLogEntries(true, true, true).Values.Last(), Is.EqualTo(1000));
    }
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task MissingRequiredChunkIsReportedAsCorruption(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    using var wal = Open(Provider(files), 1, Options(mode));
    for (var key = 0; key < 80; ++key) wal.Append(key, key, key);
    await wal.SyncAsync();
    var image = files.SimulatePowerLoss();
    var chunks = image.GetFiles("data/seg/shared").Where(path => path.EndsWith(".wal", StringComparison.Ordinal)).Order().ToArray();
    image.DeleteFile(chunks[1]);
    Assert.Throws<WriteAheadLogCorruptionException>(() => Open(Provider(image), 1, Options(mode)));
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task CrashDuringCheckpointAndChunkDeletionKeepsSyncedData(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    using var tree = Factory(files, mode).Create();
    for (var key = 0; key < 120; ++key) tree.Upsert(key, key + 1);
    await tree.SyncWALAsync();
    tree.Maintenance.MoveMutableSegmentForward();
    var images = new List<PowerLossFileStreamProvider>();
    files.OnDurableChange = () => images.Add(files.SimulatePowerLoss());
    try { tree.Maintenance.StartMergeOperation().Join(); }
    finally { files.OnDurableChange = null; }
    Assert.That(images, Is.Not.Empty);
    foreach (var image in images)
    {
      using var recovered = Factory(image, mode).Open();
      for (var key = 0; key < 120; ++key)
      {
        Assert.That(recovered.TryGet(key, out var value), Is.True, $"key {key}");
        Assert.That(value, Is.EqualTo(key + 1));
      }
    }
  }

  static IEnumerable<TestCaseData> LegacyCases()
  {
    foreach (var mode in new[] { WriteAheadLogMode.Shared, WriteAheadLogMode.AsyncShared })
    {
      foreach (var legacy in new[] { WriteAheadLogMode.Sync, WriteAheadLogMode.SyncCompressed, WriteAheadLogMode.AsyncCompressed })
      {
        foreach (var method in new[] { CompressionMethod.None, CompressionMethod.LZ4, CompressionMethod.Zstd })
          if (legacy != WriteAheadLogMode.Sync || method == CompressionMethod.None)
            yield return new TestCaseData(mode, $"Legacy208Wals/{legacy}-{method}", 150);
        yield return new TestCaseData(mode, $"LegacyCrcWals/{legacy}", 4);
      }
    }
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task LargeRecordsCrossBlocksAndChunks(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    var provider = Provider(files);
    var options = Options(mode, CompressionMethod.LZ4);
    var payload = new byte[10000];
    new Random(31).NextBytes(payload);
    using var wal = provider.GetOrCreateWAL(1, "seg", options, new Int32Serializer(), new ByteArraySerializer());
    for (var key = 0; key < 5; ++key) wal.Append(key, payload, key);
    await wal.SyncAsync();
    using var recovered = Provider(files.SimulatePowerLoss()).GetOrCreateWAL(1, "seg", options,
        new Int32Serializer(), new ByteArraySerializer());
    var read = recovered.ReadLogEntries(true, true, true);
    Assert.That(read.Keys, Is.EqualTo(Enumerable.Range(0, 5)));
    Assert.That(read.Values.All(value => value.Span.SequenceEqual(payload)), Is.True);
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task AppendsContinueDuringSyncAndConcurrentWaitersShareIt(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    using var wal = Open(Provider(files), 1, Options(mode));
    wal.Append(1, 2, 1);
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var mainSyncs = 0;
    files.BeforeDiskSync = path =>
    {
      if (!path.EndsWith(".wal", StringComparison.Ordinal)) return;
      Interlocked.Increment(ref mainSyncs);
      entered.Set();
      if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
    };
    var sync = Task.Run(async () => await wal.SyncAsync());
    Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
    try
    {
      var append = Task.Run(() => wal.Append(2, 3, 2));
      await append.WaitAsync(TimeSpan.FromSeconds(10));
    }
    finally { release.Set(); }
    await sync;
    await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () => await wal.SyncAsync())));
    files.BeforeDiskSync = null;
    Assert.That(mainSyncs, Is.EqualTo(2));
    using var recovered = Open(Provider(files.SimulatePowerLoss()), 1, Options(mode));
    Assert.That(recovered.ReadLogEntries(true, true, true).Values, Is.EqualTo(new[] { 2, 3 }));
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task NativeMappedTailRoundTrip(WriteAheadLogMode mode)
  {
    var directory = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory,
        ".tmp", nameof(SharedWriteAheadLogTests), Guid.NewGuid().ToString("N")));
    try
    {
      ZoneTreeFactory<int, int> Create() => Factory(new LocalFileStreamProvider(), mode).SetDataDirectory(directory);
      using (var tree = Create().Create())
      {
        for (var key = 0; key < 200; ++key) tree.Upsert(key, key + 1);
        await tree.SyncWALAsync();
        tree.Maintenance.MoveMutableSegmentForward();
        tree.Maintenance.StartMergeOperation().Join();
        tree.Upsert(201, 202);
        using var backup = tree.CreateLiveBackup(Path.Combine(directory, "backup"));
        backup.Start();
        backup.CreateGeneration();
        backup.Stop();
      }
      using var recovered = Create().Open();
      for (var key = 0; key < 200; ++key)
      {
        Assert.That(recovered.TryGet(key, out var value), Is.True);
        Assert.That(value, Is.EqualTo(key + 1));
      }
      Assert.That(recovered.TryGet(201, out var last), Is.True);
      Assert.That(last, Is.EqualTo(202));
      using var restored = await Factory(new LocalFileStreamProvider(), mode)
          .SetDataDirectory(Path.Combine(directory, "restored"))
          .RestoreFromLatestLiveBackup(Path.Combine(directory, "backup"));
      Assert.That(restored.TryGet(201, out var restoredValue), Is.True);
      Assert.That(restoredValue, Is.EqualTo(202));
      for (var key = 0; key < 200; ++key)
        Assert.That(restored.TryGet(key, out _), Is.True);
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
  }

  [TestCaseSource(nameof(LegacyCases))]
  public async Task ReleasedWalAndTailMigrateToSharedJournal(WriteAheadLogMode mode, string fixture, int count)
  {
    var files = new PowerLossFileStreamProvider();
    CopyFixture(files, fixture);
    using (var tree = Factory(files, mode).Open())
    {
      if (count == 150)
        for (var key = 0; key < count; ++key)
        {
          Assert.That(tree.TryGet(key, out var value), Is.True);
          Assert.That(value, Is.EqualTo(key + 1));
        }
      else
      {
        Assert.That(tree.TryGet(2, out var legacyValue), Is.True);
        Assert.That(legacyValue, Is.EqualTo(999));
      }
      tree.Upsert(1000, 1001);
      await tree.SyncWALAsync();
    }
    using var recovered = Factory(files.SimulatePowerLoss(), WriteAheadLogMode.AsyncCompressed).Open();
    Assert.That(recovered.TryGet(1000, out var actual), Is.True);
    Assert.That(actual, Is.EqualTo(1001));
    Assert.That(files.GetFiles("data/seg").Any(path => path.Contains(".wal_", StringComparison.Ordinal)), Is.False);
  }

  static void CopyFixture(PowerLossFileStreamProvider files, string fixture)
  {
    var source = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", fixture);
    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
    {
      var target = Path.Combine("data", Path.GetRelativePath(source, file));
      files.CreateDirectory(Path.GetDirectoryName(target));
      using var stream = files.CreateFileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
      stream.Write(File.ReadAllBytes(file));
      stream.Flush(true);
    }
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public void InterruptedLegacyMigrationKeepsTheReleasedRecords(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    CopyFixture(files, "Legacy208Wals/SyncCompressed-Zstd");
    var images = new List<PowerLossFileStreamProvider>();
    var random = new Random(193);
    void Capture()
    {
      lock (images)
        images.Add(files.SimulateTornPowerLoss(random));
    }
    files.BeforeDiskSync = _ => Capture();
    files.OnDurableChange = Capture;
    try { using var migrated = Factory(files, mode).Open(); }
    finally { files.BeforeDiskSync = null; files.OnDurableChange = null; }
    Assert.That(images, Is.Not.Empty);
    foreach (var image in images)
    {
      using var recovered = Factory(image, mode).Open();
      for (var key = 0; key < 150; ++key)
      {
        Assert.That(recovered.TryGet(key, out var value), Is.True, $"key {key}");
        Assert.That(value, Is.EqualTo(key + 1));
      }
    }
  }
}
