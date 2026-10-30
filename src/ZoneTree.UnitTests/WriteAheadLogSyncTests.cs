using System.Collections.Concurrent;
using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

/// <summary>
/// SyncWALAsync acknowledges that completed writes survive a power loss.
/// These tests open the tree from the files a power loss leaves.
/// </summary>
public sealed class WriteAheadLogSyncTests
{
  const int WalBlockSize = 256;

  static ZoneTreeFactory<int, int> CreateFactory(
      IFileStreamProvider provider,
      WriteAheadLogMode mode,
      bool enableGarbageCollection = false,
      int emptyQueuePollInterval = 100)
  {
    return new ZoneTreeFactory<int, int>(provider)
        .Configure(options =>
        {
          options.AllowUnsafeOptionValues = true;
          options.EnableSingleSegmentGarbageCollection = enableGarbageCollection;
        })
        .ConfigureWriteAheadLogOptions(options =>
        {
          options.WriteAheadLogMode = mode;
          options.CompressionBlockSize = WalBlockSize;
          options.AsyncCompressedModeOptions.EmptyQueuePollInterval = emptyQueuePollInterval;
        });
  }

  static int[] MissingKeys(IFileStreamProvider provider, WriteAheadLogMode mode, IEnumerable<int> keys)
  {
    using var tree = CreateFactory(provider, mode).Open();
    return keys
        .Where(key => !tree.TryGet(key, out var value) || value != key + 1)
        .ToArray();
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task SyncedWritesSurvivePowerLoss(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    var keys = Enumerable.Range(0, 1000).ToArray();
    PowerLossFileStreamProvider afterPowerLoss;
    using (var tree = CreateFactory(provider, mode).Create())
    {
      foreach (var key in keys)
        tree.Upsert(key, key + 1);
      await tree.SyncWALAsync();
      afterPowerLoss = provider.SimulatePowerLoss();
    }
    Assert.That(MissingKeys(afterPowerLoss, mode, keys), Is.Empty);
  }

  [Test]
  public void UnsyncedWritesOfSyncModeDoNotSurvivePowerLoss()
  {
    // Sync mode flushes records to the operating system but not to the disk.
    // This shows that the power loss simulation drops unsynced records.
    var provider = new PowerLossFileStreamProvider();
    var keys = Enumerable.Range(0, 100).ToArray();
    PowerLossFileStreamProvider afterPowerLoss;
    using (var tree = CreateFactory(provider, WriteAheadLogMode.Sync).Create())
    {
      foreach (var key in keys)
        tree.Upsert(key, key + 1);
      afterPowerLoss = provider.SimulatePowerLoss();
    }
    Assert.That(MissingKeys(afterPowerLoss, WriteAheadLogMode.Sync, keys), Is.EqualTo(keys));
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task SyncedWritesSurvivePowerLossAcrossSegmentRotationAndMerge(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    var keys = Enumerable.Range(0, 400).ToArray();
    PowerLossFileStreamProvider afterPowerLoss;
    using (var tree = CreateFactory(provider, mode).Create())
    {
      foreach (var key in keys.Take(100))
        tree.Upsert(key, key + 1);
      tree.Maintenance.MoveMutableSegmentForward();
      foreach (var key in keys.Skip(100).Take(100))
        tree.Upsert(key, key + 1);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
      foreach (var key in keys.Skip(200).Take(100))
        tree.Upsert(key, key + 1);
      // The frozen segment can still be closing its WAL on another thread.
      tree.Maintenance.MoveMutableSegmentForward();
      foreach (var key in keys.Skip(300))
        tree.Upsert(key, key + 1);
      await tree.SyncWALAsync();
      afterPowerLoss = provider.SimulatePowerLoss();
    }
    Assert.That(MissingKeys(afterPowerLoss, mode, keys), Is.Empty);
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task SyncedWritesSurviveAPowerLossRightAfterSegmentRotation(WriteAheadLogMode mode)
  {
    for (var i = 0; i < 20; ++i)
    {
      var provider = new PowerLossFileStreamProvider();
      var keys = Enumerable.Range(0, 50).ToArray();
      PowerLossFileStreamProvider afterPowerLoss;
      using (var tree = CreateFactory(provider, mode).Create())
      {
        foreach (var key in keys)
          tree.Upsert(key, key + 1);
        tree.Maintenance.MoveMutableSegmentForward();
        await tree.SyncWALAsync();
        afterPowerLoss = provider.SimulatePowerLoss();
      }
      Assert.That(MissingKeys(afterPowerLoss, mode, keys), Is.Empty);
    }
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task ConcurrentWritersGetDurableAcknowledgements(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    var acknowledgedKeys = new ConcurrentBag<int>();
    PowerLossFileStreamProvider afterPowerLoss;
    var syncCount = 0;
    using (var tree = CreateFactory(provider, mode).Create())
    {
      var stopRotation = false;
      var rotation = Task.Run(async () =>
      {
        while (!Volatile.Read(ref stopRotation))
        {
          tree.Maintenance.MoveMutableSegmentForward();
          await Task.Delay(5);
        }
      });
      var writers = Enumerable.Range(0, 8).Select(writer => Task.Run(async () =>
      {
        for (var i = 0; i < 300; ++i)
        {
          var key = writer * 1000 + i;
          tree.Upsert(key, key + 1);
          if (i % 3 != 0)
            continue;
          await tree.SyncWALAsync();
          Interlocked.Increment(ref syncCount);
          // The sync acknowledges every write of this writer so far.
          for (var j = i; j >= 0 && j > i - 3; --j)
            acknowledgedKeys.Add(writer * 1000 + j);
        }
      })).ToArray();
      await Task.WhenAll(writers);
      Volatile.Write(ref stopRotation, true);
      await rotation;
      afterPowerLoss = provider.SimulatePowerLoss();
    }
    Assert.That(acknowledgedKeys, Is.Not.Empty);
    Assert.That(MissingKeys(afterPowerLoss, mode, acknowledgedKeys.Distinct()), Is.Empty);
    TestContext.Out.WriteLine($"{syncCount} sync calls, {provider.DiskSyncCount} disk syncs.");
  }

  // Sync mode is not tested here: its appends wait while the file is synced,
  // so how many writers share a sync depends on thread scheduling.
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public void ConcurrentSyncCallsShareDiskSyncs(WriteAheadLogMode mode)
  {
    const int writerCount = 16;
    const int writeCount = 50;
    var provider = new PowerLossFileStreamProvider();
    using var tree = CreateFactory(provider, mode).Create();
    var walDiskSyncCount = 0;
    provider.BeforeDiskSync = path =>
    {
      if (!path.Contains("/seg/", StringComparison.Ordinal))
        return;
      Interlocked.Increment(ref walDiskSyncCount);
      // A disk sync takes time, so writers queue behind it.
      Thread.Sleep(1);
    };
    var writers = Enumerable.Range(0, writerCount).Select(writer => new Thread(() =>
    {
      for (var i = 0; i < writeCount; ++i)
      {
        var key = writer * 1000 + i;
        tree.Upsert(key, key + 1);
        tree.SyncWALAsync().GetAwaiter().GetResult();
      }
    })).ToArray();
    foreach (var writer in writers)
      writer.Start();
    foreach (var writer in writers)
      writer.Join();
    provider.BeforeDiskSync = null;
    TestContext.Out.WriteLine($"{writerCount * writeCount} sync calls, {walDiskSyncCount} WAL disk syncs.");
    Assert.That(walDiskSyncCount, Is.LessThan(writerCount * writeCount / 2));
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task GarbageCollectionOnLoadKeepsSyncedWritesAtEveryPowerLossPoint(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    var keys = Enumerable.Range(0, 300).ToArray();
    var keptKeys = keys.Where(key => key % 3 != 0).ToArray();
    var deletedKeys = keys.Where(key => key % 3 == 0).ToArray();
    using (var tree = CreateFactory(provider, mode, true).Create())
    {
      foreach (var key in keys)
        tree.Upsert(key, key + 1);
      foreach (var key in deletedKeys)
        tree.ForceDelete(key);
      await tree.SyncWALAsync();
    }

    // Opening the tree compacts its mutable segment WAL.
    // Take the files a power loss leaves at every disk sync,
    // replacement and deletion while the tree opens.
    var powerLossPoints = new ConcurrentQueue<PowerLossFileStreamProvider>();
    provider.OnDurableChange = () => powerLossPoints.Enqueue(provider.SimulatePowerLoss());
    using (var tree = CreateFactory(provider, mode, true).Open())
    {
      provider.OnDurableChange = null;
      Assert.That(tree.Maintenance.MutableSegmentRecordCount, Is.EqualTo(keptKeys.Length));
    }
    Assert.That(provider.ReplacedFiles.Where(x => x.Contains("/seg/", StringComparison.Ordinal)), Is.Not.Empty);
    Assert.That(powerLossPoints, Is.Not.Empty);

    foreach (var afterPowerLoss in powerLossPoints)
    {
      using var tree = CreateFactory(afterPowerLoss, mode).Open();
      Assert.That(keptKeys.Where(key => !tree.TryGet(key, out var value) || value != key + 1), Is.Empty);
      Assert.That(deletedKeys.Where(key => tree.ContainsKey(key)), Is.Empty);
    }
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task SyncWALAsyncKeepsFailingAfterADiskSyncFails(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    using var tree = CreateFactory(provider, mode).Create();
    tree.Upsert(1, 2);
    await tree.SyncWALAsync();

    var failDiskSync = true;
    provider.FailDiskSync = path => Volatile.Read(ref failDiskSync) && path.Contains("/seg/", StringComparison.Ordinal);
    // A failed disk sync can drop the record that this write appends.
    try
    {
      tree.Upsert(2, 3);
    }
    catch (IOException)
    {
    }
    Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(() => tree.SyncWALAsync());

    Volatile.Write(ref failDiskSync, false);
    tree.Upsert(3, 4);
    Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(() => tree.SyncWALAsync());

    // The next segment has a new WAL, but the failed one is not merged yet.
    tree.Maintenance.MoveMutableSegmentForward();
    Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(() => tree.SyncWALAsync());

    // The merge moves the records from memory into a synced disk segment.
    tree.Maintenance.StartMergeOperation().Join();
    tree.Upsert(4, 5);
    await tree.SyncWALAsync();
  }

  /// <summary>
  /// Serializes integers, and fails for one value.
  /// </summary>
  sealed class FailingValueSerializer(int failingValue) : ISerializer<int>
  {
    readonly Int32Serializer Inner = new();

    public int Deserialize(Memory<byte> bytes) => Inner.Deserialize(bytes);

    public Memory<byte> Serialize(in int entry)
    {
      if (entry == failingValue)
        throw new InvalidOperationException("The serializer failed.");
      return Inner.Serialize(in entry);
    }
  }

  [Test]
  public void AsyncWriteAheadLogKeepsOnlyTheRecordsBeforeAFailedOne()
  {
    // A later record, such as a transaction's commit, must not survive without
    // an earlier one. The background writer serializes the records.
    const int failingValue = -1;
    var provider = new InMemoryFileStreamProvider();
    using (var tree = CreateFactory(provider, WriteAheadLogMode.AsyncCompressed)
        .SetValueSerializer(new FailingValueSerializer(failingValue))
        .Create())
    {
      for (var key = 0; key < 100; ++key)
        tree.Upsert(key, key == 50 ? failingValue : key + 1);
      Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(() => tree.SyncWALAsync());
    }
    using var reopened = CreateFactory(provider, WriteAheadLogMode.AsyncCompressed)
        .SetValueSerializer(new FailingValueSerializer(failingValue))
        .Open();
    var present = Enumerable.Range(0, 100).Where(key => reopened.TryGet(key, out _)).ToArray();
    Assert.That(present, Is.EqualTo(Enumerable.Range(0, 50).ToArray()));
  }

  [Test]
  public async Task SyncWALAsyncFailsInsteadOfWaitingWhenTheLoggerThrows()
  {
    var provider = new PowerLossFileStreamProvider();
    using var tree = CreateFactory(provider, WriteAheadLogMode.AsyncCompressed)
        .SetLogger(new ThrowingLogger())
        .Create();
    var failDiskSync = true;
    provider.FailDiskSync = path => Volatile.Read(ref failDiskSync) && path.Contains("/seg/", StringComparison.Ordinal);
    tree.Upsert(1, 2);
    var failedSync = tree.SyncWALAsync();
    Assert.That(await Task.WhenAny(failedSync, Task.Delay(TimeSpan.FromSeconds(30))), Is.SameAs(failedSync));
    Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(() => failedSync);

    // The writer keeps running, so later requests are answered too.
    Volatile.Write(ref failDiskSync, false);
    tree.Upsert(2, 3);
    var laterSync = tree.SyncWALAsync();
    Assert.That(await Task.WhenAny(laterSync, Task.Delay(TimeSpan.FromSeconds(30))), Is.SameAs(laterSync));
    Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(() => laterSync);
  }

  sealed class ThrowingLogger : ILogger
  {
    public LogLevel LogLevel { get; set; } = LogLevel.Error;

    public void LogError(Exception log) => throw new InvalidOperationException("The logger failed.");

    public void LogWarning(object log) => throw new InvalidOperationException("The logger failed.");

    public void LogInfo(object log)
    {
    }

    public void LogTrace(object log)
    {
    }
  }

  [Test]
  public async Task SyncWALAsyncDoesNotWaitForTheEmptyQueuePollInterval()
  {
    var provider = new PowerLossFileStreamProvider();
    using var tree = CreateFactory(provider, WriteAheadLogMode.AsyncCompressed, emptyQueuePollInterval: 600_000).Create();
    tree.Upsert(1, 2);
    var sync = tree.SyncWALAsync();
    var completed = await Task.WhenAny(sync, Task.Delay(TimeSpan.FromSeconds(30)));
    Assert.That(completed, Is.SameAs(sync));
    await sync;
  }

  [Test]
  public async Task SyncWALAsyncCanBeCancelledWhileTheDiskSyncRuns()
  {
    var provider = new PowerLossFileStreamProvider();
    using var tree = CreateFactory(provider, WriteAheadLogMode.AsyncCompressed).Create();
    using var diskSyncStarted = new ManualResetEventSlim();
    using var releaseDiskSync = new ManualResetEventSlim();
    provider.BeforeDiskSync = path =>
    {
      if (!path.Contains("/seg/", StringComparison.Ordinal))
        return;
      diskSyncStarted.Set();
      releaseDiskSync.Wait();
    };
    tree.Upsert(1, 2);
    using var cancellation = new CancellationTokenSource();
    var cancelledSync = tree.SyncWALAsync(cancellation.Token);
    Task sync;
    try
    {
      Assert.That(diskSyncStarted.Wait(TimeSpan.FromSeconds(30)), Is.True);
      sync = tree.SyncWALAsync();
      cancellation.Cancel();
      Assert.CatchAsync<OperationCanceledException>(() => cancelledSync);
      Assert.That(sync.IsCompleted, Is.False);
    }
    finally
    {
      provider.BeforeDiskSync = null;
      releaseDiskSync.Set();
    }
    await sync;
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task SyncWALAsyncCompletesAfterTheTreeIsDisposed(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    var tree = CreateFactory(provider, mode).Create();
    tree.Upsert(1, 2);
    var syncBeforeDispose = tree.SyncWALAsync();
    tree.Dispose();
    await syncBeforeDispose;
    await tree.SyncWALAsync();
    Assert.That(MissingKeys(provider.SimulatePowerLoss(), mode, [1]), Is.Empty);
  }

  [Test]
  public void SyncWALAsyncCompletesAtOnceInNoneMode()
  {
    var provider = new PowerLossFileStreamProvider();
    using var tree = CreateFactory(provider, WriteAheadLogMode.None).Create();
    tree.Upsert(1, 2);
    Assert.That(tree.SyncWALAsync().IsCompletedSuccessfully, Is.True);
  }

  [Test]
  public void CompressedFileStreamSealTailMovesTheTailIntoTheMainFile()
  {
    var provider = new PowerLossFileStreamProvider();
    var bytes = Enumerable.Range(0, 3 * WalBlockSize / 2).Select(x => (byte)x).ToArray();
    using (var stream = CreateCompressedFileStream(provider))
    {
      stream.Write(bytes);
      stream.SealTail();
      // The stream stays writable after sealing.
      stream.Write(bytes);
      stream.SealTail();
    }
    var tail = provider.ReadAllBytes("data/sealed.tail");
    Assert.That(BitConverter.ToInt32(tail, sizeof(int)), Is.Zero);

    // The main file holds the content without the tail file.
    var afterPowerLoss = provider.SimulatePowerLoss();
    afterPowerLoss.DeleteFile("data/sealed.tail");
    using (var stream = CreateCompressedFileStream(afterPowerLoss))
    {
      var content = new byte[stream.Length];
      stream.Seek(0, SeekOrigin.Begin);
      stream.ReadExactly(content);
      Assert.That(content, Is.EqualTo(bytes.Concat(bytes).ToArray()));
    }
  }

  [Test]
  public void CompressedFileStreamReplaceContentSyncsTheNewContentOnce()
  {
    var provider = new PowerLossFileStreamProvider();
    var newBytes = Enumerable.Range(0, 40 * WalBlockSize).Select(x => (byte)x).ToArray();
    using var stream = CreateCompressedFileStream(provider);
    stream.Write(newBytes.AsSpan(0, WalBlockSize / 2));
    var diskSyncCount = provider.DiskSyncCount;
    stream.ReplaceContent(provider, binaryWriter => binaryWriter.Write(newBytes));
    // Sealing the old tail syncs the main and the tail file.
    // Sealing the temporary file syncs its main and its tail file.
    Assert.That(provider.DiskSyncCount - diskSyncCount, Is.EqualTo(4));
  }

  [Test]
  public async Task SyncCompressedAppendsDoNotWaitForTheTailDiskSync()
  {
    var provider = new PowerLossFileStreamProvider();
    using var tree = CreateFactory(provider, WriteAheadLogMode.SyncCompressed).Create();
    using var diskSyncStarted = new ManualResetEventSlim();
    using var releaseDiskSync = new ManualResetEventSlim();
    provider.BeforeDiskSync = path =>
    {
      if (!path.EndsWith(".tail", StringComparison.Ordinal))
        return;
      diskSyncStarted.Set();
      releaseDiskSync.Wait();
    };
    tree.Upsert(1, 2);
    var sync = Task.Run(() => tree.SyncWALAsync());
    try
    {
      Assert.That(diskSyncStarted.Wait(TimeSpan.FromSeconds(30)), Is.True);
      var append = Task.Run(() => tree.Upsert(2, 3));
      Assert.That(append.Wait(TimeSpan.FromSeconds(10)), Is.True);
    }
    finally
    {
      provider.BeforeDiskSync = null;
      releaseDiskSync.Set();
    }
    await sync;
    await tree.SyncWALAsync();
    Assert.That(MissingKeys(provider.SimulatePowerLoss(), WriteAheadLogMode.SyncCompressed, [1, 2]), Is.Empty);
  }

  [TestCase(5 * WalBlockSize / 2, WalBlockSize / 3)]
  [TestCase(5 * WalBlockSize / 2, 0)]
  [TestCase(WalBlockSize / 2, 7 * WalBlockSize / 2)]
  public void CompressedFileStreamReplaceContentLeavesOldOrNewContentAtEveryPowerLossPoint(
      int oldLength, int newLength)
  {
    var provider = new PowerLossFileStreamProvider();
    var oldBytes = Enumerable.Range(0, oldLength).Select(x => (byte)x).ToArray();
    var newBytes = Enumerable.Range(0, newLength).Select(x => (byte)(255 - x)).ToArray();
    using (var stream = CreateCompressedFileStream(provider))
      stream.Write(oldBytes);

    var powerLossPoints = new ConcurrentQueue<PowerLossFileStreamProvider>();
    provider.OnDurableChange = () => powerLossPoints.Enqueue(provider.SimulatePowerLoss());
    using (var stream = CreateCompressedFileStream(provider))
    {
      var length = stream.ReplaceContent(provider, binaryWriter => binaryWriter.Write(newBytes));
      Assert.That(length, Is.EqualTo(newLength));
    }
    provider.OnDurableChange = null;
    powerLossPoints.Enqueue(provider.SimulatePowerLoss());

    var newContentCount = 0;
    foreach (var afterPowerLoss in powerLossPoints)
    {
      using var stream = CreateCompressedFileStream(afterPowerLoss);
      var content = new byte[stream.Length];
      stream.Seek(0, SeekOrigin.Begin);
      stream.ReadExactly(content);
      if (content.SequenceEqual(newBytes))
        ++newContentCount;
      else
        Assert.That(content, Is.EqualTo(oldBytes));
    }
    Assert.That(newContentCount, Is.GreaterThan(0));
  }

  [Test]
  public void CompressedFileStreamSyncToDiskIncludesTheReopenedMainFile()
  {
    // Bytes that an earlier process wrote can still be unsynced.
    // Records appended after them depend on them.
    var provider = new PowerLossFileStreamProvider();
    var bytes = Enumerable.Range(0, 3 * WalBlockSize / 2).Select(x => (byte)x).ToArray();
    using (var stream = CreateCompressedFileStream(provider))
      stream.Write(bytes);
    var diskSyncCount = provider.DiskSyncCount;
    using (var stream = CreateCompressedFileStream(provider))
    {
      stream.Seek(0, SeekOrigin.End);
      stream.Write(bytes.AsSpan(0, 10));
      stream.SyncToDisk();
      // The tail and the main file.
      Assert.That(provider.DiskSyncCount - diskSyncCount, Is.EqualTo(2));
      stream.SyncToDisk();
      Assert.That(provider.DiskSyncCount - diskSyncCount, Is.EqualTo(2));
    }
  }

  static CompressedFileStream CreateCompressedFileStream(IFileStreamProvider provider)
  {
    provider.CreateDirectory("data");
    return new CompressedFileStream(
        new ConsoleLogger(),
        provider,
        "data/sealed",
        WalBlockSize,
        false,
        0,
        CompressionMethod.LZ4,
        CompressionLevels.LZ4Fastest);
  }
}
