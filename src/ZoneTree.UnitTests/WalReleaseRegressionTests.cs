using System.Reflection;
using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class WalReleaseRegressionTests
{
  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task ExplicitSyncProtectsTransactionsWithoutBlockingSegmentRotation(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    ZoneTreeFactory<int, int> Factory(IFileStreamProvider files) => new ZoneTreeFactory<int, int>(files)
        .SetDataDirectory("data")
        .SetLogger(SilentTestLogger.Instance)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = mode);
    using var tree = Factory(provider).CreateTransactional();
    using var transaction = tree.BeginTransaction();
    transaction.Upsert(1, 10);
    var storage = tree.Maintenance.ZoneTree.Maintenance;
    var previousSegmentId = storage.MutableSegment.SegmentId;
    using var syncStarted = new ManualResetEventSlim();
    using var releaseSync = new ManualResetEventSlim();
    provider.BeforeDiskSync = path =>
    {
      if (!path.Contains($"/seg/{previousSegmentId}.", StringComparison.Ordinal))
        return;
      syncStarted.Set();
      if (!releaseSync.Wait(TimeSpan.FromSeconds(30)))
        throw new TimeoutException("Previous segment sync was not released.");
    };
    var move = Task.Run(() => storage.MoveMutableSegmentForward());
    Task sync = null;
    try
    {
      Assert.That(syncStarted.Wait(TimeSpan.FromSeconds(30)), Is.True);
      // Rotation must finish even while the previous segment's final fsync waits.
      await move.WaitAsync(TimeSpan.FromSeconds(5));
      transaction.Upsert(2, 20);
      transaction.Commit();
      sync = Task.Run(() => tree.SyncWALAsync());
      await Task.WhenAny(sync, Task.Delay(100));
      Assert.That(sync.IsCompleted, Is.False, "explicit sync must cover the previous segment too");
    }
    finally
    {
      releaseSync.Set();
    }
    await sync.WaitAsync(TimeSpan.FromSeconds(30));
    provider.BeforeDiskSync = null;
    using var recovered = Factory(provider.SimulatePowerLoss()).OpenTransactional();
    using var reader = recovered.BeginReadOnlyTransaction();
    Assert.That(reader.TryGet(1, out var first), Is.True);
    Assert.That(first, Is.EqualTo(10));
    Assert.That(reader.TryGet(2, out var second), Is.True);
    Assert.That(second, Is.EqualTo(20));
  }

  [TestCase(CompressionMethod.None, false)]
  [TestCase(CompressionMethod.LZ4, false)]
  [TestCase(CompressionMethod.Zstd, false)]
  [TestCase(CompressionMethod.None, true)]
  [TestCase(CompressionMethod.LZ4, true)]
  [TestCase(CompressionMethod.Zstd, true)]
  public async Task ConcurrentSyncDoesNotCheckpointPartOfALargeRecord(CompressionMethod compression, bool backgroundFlush)
  {
    var provider = new PowerLossFileStreamProvider();
    var options = new WriteAheadLogOptions { CompressionBlockSize = 4096, CompressionMethod = compression };
    options.SyncCompressedModeOptions.EnableTailWriterJob = false;
    SyncCompressedFileSystemWriteAheadLog<int, string> Open(IFileStreamProvider files) => new(
        SilentTestLogger.Instance, files, new Int32Serializer(), new UnicodeStringSerializer(),
        "record.wal_xxh3.1", options);
    using var wal = Open(provider);
    wal.Append(0, "complete before sync", 0);
    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(object instance, string name) => instance.GetType().GetField(name, flags).GetValue(instance);
    var stream = (CompressedFileStream)Field(wal, "FileStream");
    var tailLock = (Lock)Field(stream, "TailLock");
    var syncGroup = Field(stream, "SyncGroup");
    using var paused = new ManualResetEventSlim();
    using var resume = new ManualResetEventSlim();
    // Pause between BinaryWriter calls, after the stream's per-write lock is
    // released. The WAL must protect the complete record from checkpointing.
    using var writer = new PausingBinaryWriter(stream, paused, resume);
    wal.GetType().GetField("BinaryWriter", flags).SetValue(wal, writer);
    Task sync = null;
    Task append = null;
    PowerLossFileStreamProvider crash = null;
    try
    {
      using (tailLock.EnterScope())
      {
        sync = Task.Run(async () =>
        {
          if (backgroundFlush)
            stream.WriteTail();
          else
            await wal.SyncAsync();
        });
        Assert.That(SpinWait.SpinUntil(() => (bool)Field(syncGroup, "IsSyncRunning"),
            TimeSpan.FromSeconds(30)), Is.True);
        append = Task.Run(() => wal.Append(1, new string('x', 300), 1));
        Assert.That(paused.Wait(TimeSpan.FromSeconds(30)), Is.True);
      }
      if (await Task.WhenAny(sync, Task.Delay(200)) == sync)
      {
        await sync;
        crash = provider.SimulatePowerLoss();
      }
    }
    finally
    {
      resume.Set();
    }
    await append.WaitAsync(TimeSpan.FromSeconds(30));
    await sync.WaitAsync(TimeSpan.FromSeconds(30));
    crash ??= provider.SimulatePowerLoss();
    using var recovered = Open(crash);
    var result = recovered.ReadLogEntries(true, true, false);
    Assert.That(result.Success, Is.True, "a concurrent unfinished append must not corrupt an acknowledged WAL");
    Assert.That(result.Keys, Does.Contain(0));
    Assert.That(result.Values[0], Is.EqualTo("complete before sync"));
  }

  sealed class PausingBinaryWriter(Stream stream, ManualResetEventSlim paused, ManualResetEventSlim resume)
      : BinaryWriter(stream, Encoding.UTF8, true)
  {
    public override void Write(long value)
    {
      base.Write(value);
      paused.Set();
      if (!resume.Wait(TimeSpan.FromSeconds(30)))
        throw new TimeoutException("Record append was not resumed.");
    }
  }
}
