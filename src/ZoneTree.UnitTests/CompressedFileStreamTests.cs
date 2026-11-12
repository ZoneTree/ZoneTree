using ZoneTree.AbstractFileStream;
using ZoneTree.Options;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class CompressedFileStreamTests
{
  const int BlockSize = 4096;

  public enum Tail
  {
    MemoryMapped,
    LocalFile,
    InMemoryFile,
  }

  sealed class Context : IDisposable
  {
    public readonly IFileStreamProvider Provider;

    public readonly string FilePath;

    readonly string Directory;

    readonly Tail Tail;

    public Context(Tail tail, string name)
    {
      Tail = tail;
      Directory = Path.Combine("data", nameof(CompressedFileStreamTests), $"{name}.{tail}");
      Provider = tail == Tail.InMemoryFile
          ? new InMemoryFileStreamProvider()
          : new LocalFileStreamProvider();
      if (Provider.DirectoryExists(Directory))
        Provider.DeleteDirectory(Directory, true);
      Provider.CreateDirectory(Directory);
      FilePath = Path.Combine(Directory, "stream.wal");
    }

    public CompressedFileStream Open(int blockSize = BlockSize)
    {
      return new CompressedFileStream(
          SilentTestLogger.Instance,
          Provider,
          FilePath,
          blockSize,
          false,
          0,
          CompressionMethod.LZ4,
          CompressionLevels.LZ4Fastest,
          Tail == Tail.MemoryMapped);
    }

    public void Dispose()
    {
      Provider.DeleteDirectory(Directory, true);
    }
  }

  static byte[] Content(int length, int seed = 1)
  {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  static byte[] ReadAll(Stream stream)
  {
    stream.Seek(0, SeekOrigin.Begin);
    var bytes = new byte[stream.Length];
    stream.ReadExactly(bytes);
    // Position back at the end, where writes are allowed.
    stream.Seek(0, SeekOrigin.End);
    return bytes;
  }

  static IEnumerable<int> Lengths =>
  [
    0,
    1,
    BlockSize - 1,
    BlockSize,
    BlockSize + 1,
    3 * BlockSize,
    3 * BlockSize + 100,
  ];

  [Test]
  public void EmptySyncKeepsTheTailUnallocatedUntilTheFirstAppend([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(EmptySyncKeepsTheTailUnallocatedUntilTheFirstAppend));
    for (var reopen = 0; reopen < 2; ++reopen)
    {
      using var stream = context.Open();
      stream.SyncToDisk();
      stream.SyncToDisk();
      stream.WriteTail();
      Assert.That(FileLength(context.FilePath), Is.EqualTo(1));
      Assert.That(FileLength(context.FilePath + ".tail"), Is.Zero);
    }
    var content = Content(100);
    using (var stream = context.Open())
    {
      stream.Write(content);
      Assert.That(FileLength(context.FilePath + ".tail"), Is.GreaterThan(BlockSize));
      stream.SyncToDisk();
    }
    using var reopened = context.Open();
    Assert.That(ReadAll(reopened), Is.EqualTo(content));

    long FileLength(string path) => tail == Tail.InMemoryFile
        ? context.Provider.ReadAllBytes(path).LongLength
        : new FileInfo(path).Length;
  }

  [Test]
  public void EmptySyncPersistsMetadataWithoutAllocatingTheTail()
  {
    var provider = new PowerLossFileStreamProvider();
    using var stream = new CompressedFileStream(
        SilentTestLogger.Instance, provider, "empty", BlockSize, false, 0, CompressionMethod.Zstd, 0);
    stream.SyncToDisk();
    var afterPowerLoss = provider.SimulatePowerLoss();
    Assert.That(afterPowerLoss.ReadAllBytes("empty"), Is.EqualTo(new[] { (byte)((byte)CompressionMethod.Zstd | WalBlockFrame.MetadataFlag) }));
    Assert.That(afterPowerLoss.ReadAllBytes("empty.tail"), Is.Empty);
    var syncCount = provider.DiskSyncCount;
    stream.SyncToDisk();
    Assert.That(provider.DiskSyncCount, Is.EqualTo(syncCount));
  }

  [Test]
  public void ReopenedStreamReadsAllWrites(
      [Values] Tail tail,
      [ValueSource(nameof(Lengths))] int length)
  {
    using var context = new Context(tail, nameof(ReopenedStreamReadsAllWrites) + length);
    var content = Content(length);
    using (var stream = context.Open())
    {
      for (var position = 0; position < length;)
      {
        var writeLength = Math.Min(97 + position % 300, length - position);
        stream.Write(content.AsSpan(position, writeLength));
        position += writeLength;
      }
      Assert.That(stream.Length, Is.EqualTo(length));
      Assert.That(ReadAll(stream), Is.EqualTo(content));
    }
    using (var stream = context.Open())
    {
      Assert.That(stream.Length, Is.EqualTo(length));
      Assert.That(ReadAll(stream), Is.EqualTo(content));
    }
  }

  [Test]
  public void AppendsAfterReopenContinueTheTail([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(AppendsAfterReopenContinueTheTail));
    var content = Content(3 * BlockSize + 500);
    var position = 0;
    foreach (var part in new[] { 100, BlockSize + 100, 300, 2 * BlockSize })
    {
      using var stream = context.Open();
      Assert.That(stream.Length, Is.EqualTo(position));
      stream.Write(content.AsSpan(position, part));
      position += part;
    }
    using (var stream = context.Open())
      Assert.That(ReadAll(stream), Is.EqualTo(content.AsSpan(0, position).ToArray()));
  }

  [Test]
  public void WritesAfterReadingContinueAtTheEnd([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(WritesAfterReadingContinueAtTheEnd));
    var content = Content(2 * BlockSize + 100);
    using var stream = context.Open();
    stream.Write(content.AsSpan(0, BlockSize + 50));
    Assert.That(ReadAll(stream), Is.EqualTo(content.AsSpan(0, BlockSize + 50).ToArray()));
    stream.Write(content.AsSpan(BlockSize + 50));
    Assert.That(ReadAll(stream), Is.EqualTo(content));
  }

  [Test]
  public void TailIsReadableAfterClose([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(TailIsReadableAfterClose));
    var content = Content(100);
    var stream = context.Open();
    stream.Write(content);
    stream.Seek(0, SeekOrigin.Begin);
    stream.Close();
    var read = new byte[content.Length];
    stream.ReadExactly(read);
    Assert.That(read, Is.EqualTo(content));
    Assert.That(() => stream.Write(content), Throws.TypeOf<ObjectDisposedException>());
  }

  [Test]
  public void ClosedFilesCanBeDeleted([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(ClosedFilesCanBeDeleted));
    using (var stream = context.Open())
      stream.Write(Content(100));
    context.Provider.DeleteFile(context.FilePath + ".tail");
    context.Provider.DeleteFile(context.FilePath);
    Assert.That(context.Provider.FileExists(context.FilePath), Is.False);
  }

  [Test]
  public void TruncationKeepsTheRequestedLength(
      [Values] Tail tail,
      [Values(0, 10, BlockSize - 1, BlockSize, BlockSize + 10, 2 * BlockSize, 2 * BlockSize + 200)]
      int truncatedLength)
  {
    using var context = new Context(tail, nameof(TruncationKeepsTheRequestedLength) + truncatedLength);
    var content = Content(2 * BlockSize + 300);
    var appended = Content(BlockSize + 10, 2);
    var expected = content.AsSpan(0, truncatedLength).ToArray();
    using (var stream = context.Open())
    {
      stream.Write(content);
      stream.SetLength(truncatedLength);
      Assert.That(stream.Length, Is.EqualTo(truncatedLength));
      Assert.That(ReadAll(stream), Is.EqualTo(expected));
    }
    using (var stream = context.Open())
    {
      Assert.That(ReadAll(stream), Is.EqualTo(expected));
      stream.Write(appended);
    }
    using (var stream = context.Open())
      Assert.That(ReadAll(stream), Is.EqualTo(expected.Concat(appended).ToArray()));
  }

  [Test]
  public void TruncationOfALoadedTailPersists([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(TruncationOfALoadedTailPersists));
    var content = Content(BlockSize + 300);
    using (var stream = context.Open())
      stream.Write(content);
    using (var stream = context.Open())
      stream.SetLength(BlockSize + 100);
    using (var stream = context.Open())
      Assert.That(ReadAll(stream), Is.EqualTo(content.AsSpan(0, BlockSize + 100).ToArray()));
  }

  [Test]
  public void SealedTailLeavesTheContentInTheMainFile([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(SealedTailLeavesTheContentInTheMainFile));
    var content = Content(BlockSize + 300);
    using (var stream = context.Open())
    {
      stream.Write(content);
      stream.SealTail();
      // The stream stays writable after sealing.
      stream.Write(content);
      stream.SealTail();
    }
    context.Provider.DeleteFile(context.FilePath + ".tail");
    using (var stream = context.Open())
      Assert.That(ReadAll(stream), Is.EqualTo(content.Concat(content).ToArray()));
  }

  [Test]
  public void ReplacedContentKeepsNothingOfTheOldTail([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(ReplacedContentKeepsNothingOfTheOldTail));
    var content = Content(5 * BlockSize + 300);
    var replacement = Content(100, 2);
    var appended = Content(BlockSize, 3);
    using (var stream = context.Open())
      stream.Write(content);
    var opened = context.Open();
    var length = opened.ReplaceContent(context.Provider, writer => writer.Write(replacement));
    Assert.That(length, Is.EqualTo(replacement.Length));
    using (var stream = context.Open())
    {
      Assert.That(ReadAll(stream), Is.EqualTo(replacement));
      stream.Write(appended);
    }
    using (var stream = context.Open())
      Assert.That(ReadAll(stream), Is.EqualTo(replacement.Concat(appended).ToArray()));
  }

  [Test]
  public void UnchangedStreamDoesNotChangeItsFiles([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(UnchangedStreamDoesNotChangeItsFiles));
    using (var stream = context.Open())
    {
      stream.Write(Content(BlockSize + 300));
      stream.SyncToDisk();
    }
    var tailFile = context.Provider.ReadAllBytes(context.FilePath + ".tail");
    var mainFile = context.Provider.ReadAllBytes(context.FilePath);
    using (var stream = context.Open())
    {
      ReadAll(stream);
      stream.SyncToDisk();
    }
    Assert.That(context.Provider.ReadAllBytes(context.FilePath + ".tail"), Is.EqualTo(tailFile));
    Assert.That(context.Provider.ReadAllBytes(context.FilePath), Is.EqualTo(mainFile));
  }

  [Test]
  public void BlockSizeCanChangeBetweenOpens([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(BlockSizeCanChangeBetweenOpens));
    var content = Content(5000);
    using (var stream = context.Open(BlockSize * 2))
      stream.Write(content);
    using (var stream = context.Open(BlockSize))
    {
      Assert.That(ReadAll(stream), Is.EqualTo(content));
      stream.Write(content);
      Assert.That(ReadAll(stream), Is.EqualTo(content.Concat(content).ToArray()));
    }
    using (var stream = context.Open(BlockSize * 2))
    {
      Assert.That(ReadAll(stream), Is.EqualTo(content.Concat(content).ToArray()));
      stream.Write(content);
    }
    using (var stream = context.Open(BlockSize))
      Assert.That(ReadAll(stream), Is.EqualTo(content.Concat(content).Concat(content).ToArray()));
  }

  [TestCase(504, 256 * 1024)]
  [TestCase(4088, 256 * 1024)]
  [TestCase(8184, 256 * 1024)]
  [TestCase(262136, 256 * 1024)]
  [TestCase(5624, BlockSize)]
  public void SectorAlignedLegacyTailRetainsAllBytes(int length, int reopenedBlockSize)
  {
    using var context = new Context(Tail.InMemoryFile, nameof(SectorAlignedLegacyTailRetainsAllBytes) + length);
    var content = Content(length);
    using (var main = context.Provider.CreateFileStream(context.FilePath, FileMode.Create, FileAccess.Write, FileShare.None))
      main.WriteByte((byte)CompressionMethod.LZ4);
    using (var file = context.Provider.CreateFileStream(context.FilePath + ".tail", FileMode.Create, FileAccess.Write, FileShare.None))
    using (var writer = new BinaryWriter(file.ToStream()))
    {
      writer.Write(0);
      writer.Write(length);
      writer.Write(content);
    }
    using var stream = context.Open(reopenedBlockSize);
    Assert.That(stream.Length, Is.EqualTo(content.Length));
    Assert.That(ReadAll(stream), Is.EqualTo(content));
  }

  [TestCase(BlockSize, 2 * BlockSize, false)]
  [TestCase(2 * BlockSize, BlockSize, false)]
  [TestCase(BlockSize, 2 * BlockSize, true)]
  [TestCase(2 * BlockSize, BlockSize, true)]
  public void ChangedBlockSizeKeepsCheckpointAndPayload(int originalBlockSize, int reopenedBlockSize, bool syncBeforeReopen)
  {
    var provider = new PowerLossFileStreamProvider();
    var first = Content(3 * originalBlockSize + 100);
    var second = Content(3 * reopenedBlockSize + 100, 2);
    using (var stream = Open(originalBlockSize))
    {
      stream.Write(first);
      if (syncBeforeReopen)
        stream.SyncToDisk();
    }
    using (var stream = Open(reopenedBlockSize))
    {
      Assert.That(ReadAll(stream), Is.EqualTo(first));
      stream.Write(second);
      stream.SyncToDisk();
    }
    using var recovered = new CompressedFileStream(
        SilentTestLogger.Instance, provider.SimulatePowerLoss(), "resized", originalBlockSize, false, 0,
        CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    Assert.That(ReadAll(recovered), Is.EqualTo(first.Concat(second).ToArray()));

    CompressedFileStream Open(int blockSize) => new(
        SilentTestLogger.Instance, provider, "resized", blockSize, false, 0,
        CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
  }

  [Test]
  public void ShrinkingTailAllocationDoesNotReuseStaleCheckpointBytes()
  {
    var provider = new PowerLossFileStreamProvider();
    var content = Content(100);
    using (var stream = Open(2 * BlockSize))
      stream.Write(content);
    // Old unused bytes can look exactly like a checkpoint. This position is
    // inside the smaller allocation's slots, not the current larger slots.
    using (var file = provider.CreateFileStream("resized.tail", FileMode.Open, FileAccess.Write, FileShare.None))
      WalSyncCheckpoint.Write(file, 4608, 100, int.MaxValue, content.Length);

    using (var stream = Open(BlockSize))
    {
      stream.WriteByte(42);
      using var recovered = new CompressedFileStream(
          SilentTestLogger.Instance, provider.SimulatePowerLoss(), "resized", BlockSize, false, 0,
          CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
      Assert.That(ReadAll(recovered).Take(content.Length), Is.EqualTo(content));
    }

    CompressedFileStream Open(int blockSize) => new(
        SilentTestLogger.Instance, provider, "resized", blockSize, false, 0,
        CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
  }

  [Test]
  public void BackgroundFlushLeavesRecoveredContentForValidationUntilTheFirstWrite()
  {
    var provider = new PowerLossFileStreamProvider();
    var content = Content(BlockSize);
    using (var stream = Open(provider))
      stream.Write(content);
    provider.DeleteFile("recovered.tail");

    using var reopened = Open(provider);
    var recoveredTail = provider.ReadAllBytes("recovered.tail");
    // The async writer can poll immediately after opening. Do not checkpoint
    // recovered records until the WAL reader has had a chance to validate them.
    reopened.WriteTail();
    Assert.That(provider.ReadAllBytes("recovered.tail"), Is.EqualTo(recoveredTail));
    using var afterPowerLoss = Open(provider.SimulatePowerLoss());
    Assert.That(ReadAll(afterPowerLoss), Is.EqualTo(content));

    static CompressedFileStream Open(IFileStreamProvider files) => new(
        SilentTestLogger.Instance, files, "recovered", BlockSize, false, 0,
        CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
  }

  [Test]
  public void TailLengthIgnoresTheBytesAfterIt([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(TailLengthIgnoresTheBytesAfterIt));
    using (var stream = context.Open())
      stream.Write(Content(BlockSize));
    var mainContent = Content(BlockSize);

    // A tail file with a length in its header can hold other bytes after that length.
    var tailContent = Content(100, 2);
    var appended = Content(BlockSize, 3);
    using (var file = context.Provider.CreateFileStream(
        context.FilePath + ".tail", FileMode.Create, FileAccess.Write, FileShare.None))
    {
      file.Write(BitConverter.GetBytes(1));
      file.Write(BitConverter.GetBytes(tailContent.Length));
      file.Write(tailContent);
      file.Write(Content(50, 4));
    }

    var expected = mainContent.Concat(tailContent).ToArray();
    using (var stream = context.Open())
    {
      Assert.That(ReadAll(stream), Is.EqualTo(expected));
      stream.Write(appended);
    }
    using (var stream = context.Open())
      Assert.That(ReadAll(stream), Is.EqualTo(expected.Concat(appended).ToArray()));
  }

  static IFileStreamProvider CopyAsProcessCrash(IFileStreamProvider provider, params string[] paths)
  {
    var afterCrash = new InMemoryFileStreamProvider();
    afterCrash.CreateDirectory("data");
    foreach (var path in paths)
    {
      using var file = afterCrash.CreateFileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
      file.Write(provider.ReadAllBytes(path));
    }
    return afterCrash;
  }

  [Test]
  public void ProcessCrashLosesNoWriteOfAStreamWithoutMapping()
  {
    // The files of a file provider show what the operating system holds.
    var provider = new InMemoryFileStreamProvider();
    provider.CreateDirectory("data");
    var content = Content(3 * BlockSize + 100);
    using var stream = new CompressedFileStream(
        SilentTestLogger.Instance, provider, "data/crash", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    for (var position = 0; position < content.Length;)
    {
      var writeLength = Math.Min(211, content.Length - position);
      stream.Write(content.AsSpan(position, writeLength));
      position += writeLength;
      var afterCrash = CopyAsProcessCrash(provider, "data/crash", "data/crash.tail");
      using var recovered = new CompressedFileStream(
          SilentTestLogger.Instance, afterCrash, "data/crash", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
      Assert.That(ReadAll(recovered), Is.EqualTo(content.AsSpan(0, position).ToArray()));
    }
  }

  [Test]
  public void ProcessCrashDuringTailRolloverKeepsTheCommittedBlock()
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    IFileStreamProvider afterCrash = null;
    provider.AfterWrite = path =>
    {
      if (path.EndsWith(".tail", StringComparison.Ordinal) &&
          BitConverter.ToInt32(provider.ReadAllBytes(path), 0) == 1)
        afterCrash = CopyAsProcessCrash(provider, "data/rollover", "data/rollover.tail");
    };
    // Zeros without compression look like a sector that did not persist.
    var content = new byte[BlockSize];
    using var stream = new CompressedFileStream(
        SilentTestLogger.Instance, provider, "data/rollover", BlockSize, false, 0, CompressionMethod.None, 0);
    stream.Write(content);
    provider.AfterWrite = null;

    using var recovered = new CompressedFileStream(
        SilentTestLogger.Instance, afterCrash, "data/rollover", BlockSize, false, 0, CompressionMethod.None, 0);
    Assert.That(ReadAll(recovered), Is.EqualTo(content));
  }

  [Test]
  public void SyncToDiskSurvivesAPowerLoss()
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    var content = Content(3 * BlockSize + 100);
    using var stream = new CompressedFileStream(
        SilentTestLogger.Instance, provider, "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    for (var position = 0; position < content.Length;)
    {
      var writeLength = Math.Min(777, content.Length - position);
      stream.Write(content.AsSpan(position, writeLength));
      position += writeLength;
      stream.SyncToDisk();
      var afterPowerLoss = provider.SimulatePowerLoss();
      using var recovered = new CompressedFileStream(
          SilentTestLogger.Instance, afterPowerLoss, "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
      Assert.That(ReadAll(recovered), Is.EqualTo(content.AsSpan(0, position).ToArray()));
    }
  }

  [Test]
  public void SyncToDiskSyncsOnlyWhatChanged()
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    using var stream = new CompressedFileStream(
        SilentTestLogger.Instance, provider, "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    stream.Write(Content(10));
    var syncCount = provider.DiskSyncCount;
    stream.SyncToDisk();
    Assert.That(provider.DiskSyncCount - syncCount, Is.EqualTo(1));
    stream.SyncToDisk();
    Assert.That(provider.DiskSyncCount - syncCount, Is.EqualTo(1));
  }

  [Test]
  public void DurableFramesNeedExactlyOneMainFileSync([Values] CompressionMethod compression)
  {
    var provider = new PowerLossFileStreamProvider();
    using var stream = Open(provider);
    var expected = new List<byte>();
    var syncedPaths = new List<string>();
    provider.BeforeDiskSync = syncedPaths.Add;
    try
    {
      for (var commit = 0; commit < 8; ++commit)
      {
        var bytes = Content(100 + commit * 137, commit + 1);
        expected.AddRange(bytes);
        stream.Write(bytes);
        var before = provider.DiskSyncCount;
        stream.SyncToDisk();
        Assert.That(provider.DiskSyncCount - before, Is.EqualTo(1));
        stream.SyncToDisk();
        Assert.That(provider.DiskSyncCount - before, Is.EqualTo(1));
        Assert.That(syncedPaths, Is.All.EqualTo("single-sync"));
        // The strict provider has never persisted any of the tail allocation,
        // checkpoint or descriptor. Main-file frames must suffice on their own.
        var image = provider.SimulatePowerLoss();
        Assert.That(image.ReadAllBytes("single-sync.tail"), Is.Empty);
        using var recovered = Open(image);
        Assert.That(ReadAll(recovered), Is.EqualTo(expected));
      }
      // Later unsynced reuse must not damage the acknowledged immutable prefix.
      stream.Write(Content(3 * BlockSize + 100, 9));
      var random = new Random(71);
      for (var loss = 0; loss < 100; ++loss)
      {
        using var recovered = Open(provider.SimulateTornPowerLoss(random));
        Assert.That(ReadAll(recovered).Take(expected.Count), Is.EqualTo(expected));
      }
    }
    finally
    {
      provider.BeforeDiskSync = null;
    }

    CompressedFileStream Open(IFileStreamProvider files) => new(
        SilentTestLogger.Instance, files, "single-sync", BlockSize, false, 0, compression, 0);
  }

  [Test]
  public void RecoveredDurableTailSurvivesItsFirstReuse(
      [Values] CompressionMethod compression, [Values] bool explicitSync)
  {
    var provider = new PowerLossFileStreamProvider();
    var original = Content(500);
    using (var stream = Open(provider))
      stream.Write(original); // Closing makes this unfinished tail durable.
    var images = new List<PowerLossFileStreamProvider>();
    var random = new Random(83);
    void Observe()
    {
      images.Add(provider.SimulatePowerLoss());
      images.Add(provider.SimulateTornPowerLoss(random));
    }
    using (var stream = Open(provider))
    {
      provider.BeforeDiskSync = _ => Observe();
      provider.AfterWrite = _ => Observe();
      try
      {
        var syncs = provider.DiskSyncCount;
        if (explicitSync)
          stream.SyncToDisk();
        else
          stream.Write(Content(3 * BlockSize, 2));
        Assert.That(provider.DiskSyncCount - syncs, Is.EqualTo(1));
      }
      finally
      {
        provider.BeforeDiskSync = null;
        provider.AfterWrite = null;
      }
    }
    Assert.That(images, Is.Not.Empty);
    foreach (var image in images)
    {
      using var recovered = Open(image);
      Assert.That(ReadAll(recovered).Take(original.Length), Is.EqualTo(original));
    }

    CompressedFileStream Open(IFileStreamProvider files) => new(
        SilentTestLogger.Instance, files, "recovered-tail", BlockSize, false, 0, compression, 0);
  }

  [Test]
  public async Task RolloverDuringMainSyncDoesNotDeadlockOrChangeItsAcknowledgedPrefix()
  {
    var provider = new PowerLossFileStreamProvider();
    using var stream = Open(provider);
    var original = Content(100);
    var appended = Content(2 * BlockSize + 100, 2);
    stream.Write(original);
    using var started = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    provider.BeforeDiskSync = _ =>
    {
      started.Set();
      release.Wait();
    };
    var sync = Task.Run(stream.SyncToDisk);
    Task rollover = null;
    try
    {
      Assert.That(started.Wait(TimeSpan.FromSeconds(10)), Is.True);
      rollover = Task.Run(() => stream.Write(appended));
      Assert.That(SpinWait.SpinUntil(() => stream.Length >= original.Length + BlockSize,
          TimeSpan.FromSeconds(10)), Is.True, "writes can fill the new tail during the disk sync");
      Assert.That(rollover.IsCompleted, Is.False, "the rollover must wait before touching the main file");
    }
    finally
    {
      provider.BeforeDiskSync = null;
      release.Set();
    }
    await sync.WaitAsync(TimeSpan.FromSeconds(10));
    await rollover.WaitAsync(TimeSpan.FromSeconds(10));
    using (var recovered = Open(provider.SimulatePowerLoss()))
      Assert.That(ReadAll(recovered).Take(original.Length), Is.EqualTo(original));
    stream.SyncToDisk();
    using var final = Open(provider.SimulatePowerLoss());
    Assert.That(ReadAll(final), Is.EqualTo(original.Concat(appended).ToArray()));

    CompressedFileStream Open(IFileStreamProvider files) => new(
        SilentTestLogger.Instance, files, "concurrent-rollover", BlockSize, false, 0, CompressionMethod.None, 0);
  }

  [TestCase(CompressionMethod.None, 0)]
  [TestCase(CompressionMethod.LZ4, 0)]
  [TestCase(CompressionMethod.Zstd, 0)]
  [TestCase(CompressionMethod.None, 100)]
  [TestCase(CompressionMethod.LZ4, 100)]
  [TestCase(CompressionMethod.Zstd, 100)]
  public void BackgroundFlushProtectsTheWholeWalAcrossLaterRollovers(CompressionMethod compression, int tailLength)
  {
    var provider = new PowerLossFileStreamProvider();
    using var stream = Open(provider);
    var content = Content(3 * BlockSize + tailLength);
    stream.Write(content);
    stream.WriteTail();
    var syncCount = provider.DiskSyncCount;
    using (var recovered = Open(provider.SimulatePowerLoss()))
      Assert.That(ReadAll(recovered), Is.EqualTo(content), "background flush must include main-file blocks");

    stream.WriteTail();
    stream.SyncToDisk();
    Assert.That(provider.DiskSyncCount, Is.EqualTo(syncCount), "an unchanged WAL must not be synced again");

    stream.Write(Content(3 * BlockSize + 200, 2));
    using var afterRollover = Open(provider.SimulatePowerLoss());
    Assert.That(ReadAll(afterRollover), Is.EqualTo(content), "tail reuse must not destroy the background-synced prefix");

    CompressedFileStream Open(IFileStreamProvider files) => new(
        SilentTestLogger.Instance, files, "background", BlockSize, false, 0, compression, 0);
  }

  [TestCase(CompressionMethod.None, 0)]
  [TestCase(CompressionMethod.LZ4, 0)]
  [TestCase(CompressionMethod.Zstd, 0)]
  [TestCase(CompressionMethod.None, BlockSize)]
  [TestCase(CompressionMethod.LZ4, BlockSize)]
  [TestCase(CompressionMethod.Zstd, BlockSize)]
  public void AppendAfterTruncatingABackgroundSyncedWalKeepsTheTail(CompressionMethod compression, int keptLength)
  {
    var provider = new PowerLossFileStreamProvider();
    var original = Content(3 * BlockSize + 100);
    var appended = Content(100, 2);
    using (var stream = Open())
    {
      stream.Write(original);
      stream.WriteTail();
      stream.SetLength(keptLength);
      stream.Write(appended);
    }
    using var recovered = Open();
    Assert.That(ReadAll(recovered), Is.EqualTo(original.Take(keptLength).Concat(appended).ToArray()));

    CompressedFileStream Open() => new(
        SilentTestLogger.Instance, provider, "truncated", BlockSize, false, 0, compression, 0);
  }

  [Test]
  public void ExplicitSyncSurvivesAPowerLossAcrossABlockRollover()
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    var content = Content(BlockSize + 100);
    using var stream = new CompressedFileStream(
        SilentTestLogger.Instance, provider, "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    stream.Write(content);
    stream.SyncToDisk();
    // Reusing and even persisting the active tail must not destroy the synced
    // prefix, whose bytes now live in the append-only main file.
    stream.Write(Content(3 * BlockSize + 100, 2));
    stream.WriteTail();
    using var recovered = new CompressedFileStream(
        SilentTestLogger.Instance, provider.SimulatePowerLoss(), "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    Assert.That(ReadAll(recovered).Take(content.Length), Is.EqualTo(content));
  }

  [TestCase(CompressionMethod.None)]
  [TestCase(CompressionMethod.Zstd)]
  [TestCase(CompressionMethod.LZ4)]
  public void OrdinaryBlockRolloverDoesNotSyncToDisk(CompressionMethod method)
  {
    var provider = new PowerLossFileStreamProvider();
    using var stream = new CompressedFileStream(
        SilentTestLogger.Instance, provider, "rollover", BlockSize, false, 0, method, 0);
    var syncs = provider.DiskSyncCount;
    var content = Content(20 * BlockSize + 100);
    stream.Write(content);
    Assert.That(provider.DiskSyncCount, Is.EqualTo(syncs), "ordinary block commits must not fsync");
    stream.SyncToDisk();
    using var recovered = new CompressedFileStream(
        SilentTestLogger.Instance, provider.SimulatePowerLoss(), "rollover", BlockSize, false, 0, method, 0);
    Assert.That(ReadAll(recovered), Is.EqualTo(content));
  }

  [Test]
  public void RepeatedSyncsReuseTheFixedTailCheckpointRegion([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(RepeatedSyncsReuseTheFixedTailCheckpointRegion));
    var content = Content(64);
    using (var stream = context.Open())
    {
      long allocatedLength = 0;
      for (var i = 0; i < content.Length; ++i)
      {
        stream.WriteByte(content[i]);
        stream.SyncToDisk();
        var length = tail == Tail.InMemoryFile ?
            context.Provider.ReadAllBytes(context.FilePath + ".tail").Length :
            new FileInfo(context.FilePath + ".tail").Length;
        if (i == 0)
          allocatedLength = length;
        Assert.That(length, Is.EqualTo(allocatedLength), "checkpoint storage must not grow with sync count");
      }
    }
    using var reopened = context.Open();
    Assert.That(ReadAll(reopened), Is.EqualTo(content));
  }

  [Test]
  public void TornCheckpointUpdateKeepsThePreviousCheckpoint()
  {
    var provider = new InMemoryFileStreamProvider();
    var first = Content(1000);
    var second = Content(1000, 2);
    using (var stream = new CompressedFileStream(
        SilentTestLogger.Instance, provider, "checkpoint", BlockSize, false, 0, CompressionMethod.None, 0))
    {
      stream.Write(first);
      stream.SyncToDisk();
      stream.Write(second);
      stream.SyncToDisk();
    }
    var bytes = provider.ReadAllBytes("checkpoint.tail");
    // Sequence two occupies slot zero. Simulate a torn update within that
    // sector, leaving sequence one's slot in the other sector untouched.
    var offset = bytes.Length - WalSyncCheckpoint.RegionSize;
    Array.Clear(bytes, offset + 16, sizeof(long));
    using (var tailFile = provider.CreateFileStream("checkpoint.tail", FileMode.Open, FileAccess.Write, FileShare.None))
      tailFile.Write(bytes);
    using var recovered = new CompressedFileStream(
        SilentTestLogger.Instance, provider, "checkpoint", BlockSize, false, 0, CompressionMethod.None, 0);
    Assert.That(ReadAll(recovered), Is.EqualTo(first.Concat(second).ToArray()));
  }

  [TestCase(CompressionMethod.LZ4, false, BlockSize)]
  [TestCase(CompressionMethod.Zstd, false, BlockSize)]
  [TestCase(CompressionMethod.None, false, BlockSize)]
  [TestCase(CompressionMethod.LZ4, true, BlockSize)]
  [TestCase(CompressionMethod.Zstd, true, BlockSize)]
  [TestCase(CompressionMethod.None, true, BlockSize)]
  [TestCase(CompressionMethod.Zstd, true, 2 * BlockSize)]
  public void MissingDescriptorStillValidatesTornMainBlocks(
      CompressionMethod method, bool hasCheckpoint, int reopenedBlockSize)
  {
    var provider = new InMemoryFileStreamProvider();
    var prefix = hasCheckpoint ? Content(400) : [];
    long mainPrefixLength;
    using (var stream = Open(BlockSize))
    {
      if (hasCheckpoint)
      {
        stream.Write(prefix.AsSpan(0, 200));
        stream.SyncToDisk();
        stream.Write(prefix.AsSpan(200));
        stream.SyncToDisk(); // Slot zero survives when the descriptor sector is lost.
      }
      mainPrefixLength = provider.ReadAllBytes("torn-format").Length;
      stream.Write(Content(3 * BlockSize, 2));
    }
    using (var main = provider.CreateFileStream("torn-format", FileMode.Open, FileAccess.Write, FileShare.None))
    {
      main.SetLength(mainPrefixLength);
      main.Position = mainPrefixLength;
      main.Write(new byte[2 * 3 * sizeof(int)]);
    }
    var tail = provider.ReadAllBytes("torn-format.tail");
    // The size and advanced tail index persisted, but these two main headers
    // and the sector containing the format descriptor did not.
    Array.Clear(tail, tail.Length - WalSyncCheckpoint.SlotSize, WalSyncCheckpoint.SlotSize);
    using (var file = provider.CreateFileStream("torn-format.tail", FileMode.Open, FileAccess.Write, FileShare.None))
      file.Write(tail);

    using (var recovered = Open(reopenedBlockSize))
      Assert.That(ReadAll(recovered), Is.EqualTo(prefix));
    Assert.That(provider.ReadAllBytes("torn-format").LongLength, Is.EqualTo(mainPrefixLength));
    using var repairedTail = provider.CreateFileStream("torn-format.tail", FileMode.Open, FileAccess.Read, FileShare.None);
    Assert.That(WalTailFormat.ReadVersion(repairedTail), Is.EqualTo(WalTailFormat.CurrentVersion));

    CompressedFileStream Open(int blockSize) => new(
        SilentTestLogger.Instance, provider, "torn-format", blockSize, false, 0, method, 0);
  }

  // Only the size of the metadata byte, of the first sector, or of the whole file with
  // its later sectors persisted.
  [TestCase(1)]
  [TestCase(512)]
  [TestCase(int.MaxValue)]
  public void MainFileWithoutItsFirstSectorStartsOverAsANewFile(int persistedLength)
  {
    const int sectorSize = 512;
    const byte metadata = (byte)CompressionMethod.Zstd | WalBlockFrame.MetadataFlag;
    var provider = new InMemoryFileStreamProvider();
    using (var stream = Open())
    {
      stream.Write(Content(3 * BlockSize + 100));
      stream.SyncToDisk();
    }
    var main = provider.ReadAllBytes("unwritten");
    Assert.That(main[0], Is.EqualTo(metadata));
    Assert.That(main.Length, Is.GreaterThan(2 * sectorSize));
    // A power loss before the first disk sync completes can keep the size of the
    // main file without its first sector. Zero is the metadata of a released LZ4
    // file, and the tail kept an old size without the checkpoints: only its
    // header persisted, which names a later block.
    Array.Clear(main, 0, sectorSize);
    Put("unwritten", main[..Math.Min(persistedLength, main.Length)]);
    var tailHeader = provider.ReadAllBytes("unwritten.tail")[..(2 * sizeof(int))];
    Assert.That(BitConverter.ToInt32(tailHeader, 0), Is.GreaterThan(1));
    Put("unwritten.tail", tailHeader);

    var content = Content(BlockSize + 100, 2);
    using (var recovered = Open())
    {
      Assert.That(ReadAll(recovered), Is.Empty);
      recovered.Write(content);
      recovered.SyncToDisk();
    }
    Assert.That(provider.ReadAllBytes("unwritten")[0], Is.EqualTo(metadata));
    using var reopened = Open();
    Assert.That(ReadAll(reopened), Is.EqualTo(content));

    CompressedFileStream Open() => new(
        SilentTestLogger.Instance, provider, "unwritten", BlockSize, false, 0, CompressionMethod.Zstd, 0);

    void Put(string path, byte[] bytes)
    {
      using var file = provider.CreateFileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
      file.Write(bytes);
    }
  }

  [Test]
  public void ReleasedLZ4FileWithOnlyAnEmptyBlockKeepsTheRecordsOfItsTail()
  {
    var provider = new InMemoryFileStreamProvider();
    // A released LZ4 main file: the metadata zero and an empty block 0 that older
    // versions could commit. Its records are in the legacy tail, which names block 1.
    var records = Content(100);
    Put("released", new byte[1 + 3 * sizeof(int)]);
    Put("released.tail", BitConverter.GetBytes(1)
        .Concat(BitConverter.GetBytes(records.Length)).Concat(records).ToArray());

    var appended = Content(BlockSize + 100, 2);
    using (var stream = Open())
    {
      Assert.That(ReadAll(stream), Is.EqualTo(records));
      stream.Write(appended);
      stream.SyncToDisk();
    }
    using var reopened = Open();
    Assert.That(ReadAll(reopened), Is.EqualTo(records.Concat(appended).ToArray()));

    CompressedFileStream Open() => new(
        SilentTestLogger.Instance, provider, "released", BlockSize, false, 0, CompressionMethod.LZ4, 0);

    void Put(string path, byte[] bytes)
    {
      using var file = provider.CreateFileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
      file.Write(bytes);
    }
  }

  [Test]
  public void ReleasedEmptyBlockBeforeABlockSurvivesTheMigration(
      [Values(CompressionMethod.None, CompressionMethod.LZ4)] CompressionMethod method,
      [Values] bool loseCheckpoint)
  {
    var provider = new InMemoryFileStreamProvider();
    // A released main file: an empty block 0 that older versions could commit, then
    // block 1, and a legacy tail that names block 2.
    var records = Content(1680);
    var payload = Compression.DataCompression.Compress(method, 0, records).ToArray();
    Put("released", new[] { (byte)method }
        .Concat(new[] { 0, 0, 0, 1, payload.Length, records.Length }.SelectMany(BitConverter.GetBytes))
        .Concat(payload).ToArray());
    Put("released.tail", BitConverter.GetBytes(2).Concat(new byte[sizeof(int)]).ToArray());

    var appended = Content(BlockSize + 100, 2);
    using (var stream = Open())
    {
      Assert.That(ReadAll(stream), Is.EqualTo(records));
      stream.Write(appended);
      stream.SyncToDisk();
    }
    if (loseCheckpoint)
    {
      // The checkpoint is only a recovery hint, and a power loss may not keep it.
      var tail = provider.ReadAllBytes("released.tail");
      Array.Clear(tail, tail.Length - WalSyncCheckpoint.RegionSize,
          WalSyncCheckpoint.RegionSize - WalTailFormat.DescriptorSize);
      Put("released.tail", tail);
    }
    using var reopened = Open();
    Assert.That(ReadAll(reopened), Is.EqualTo(records.Concat(appended).ToArray()));

    CompressedFileStream Open() => new(
        SilentTestLogger.Instance, provider, "released", 2048, false, 0, method, 0);

    void Put(string path, byte[] bytes)
    {
      using var file = provider.CreateFileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
      file.Write(bytes);
    }
  }

  [Test]
  public void TailFormatDescriptorSurvivesCheckpointClearing([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(TailFormatDescriptorSurvivesCheckpointClearing));
    using (var stream = context.Open())
    {
      stream.Write(Content(BlockSize + 100));
      stream.SyncToDisk();
      stream.SetLength(0);
    }
    using var file = context.Provider.CreateFileStream(
        context.FilePath + ".tail", FileMode.Open, FileAccess.Read, FileShare.None);
    Assert.That(WalTailFormat.ReadVersion(file), Is.EqualTo(WalTailFormat.CurrentVersion));
  }

  [TestCase(0, true)]
  [TestCase(WalTailFormat.CurrentVersion + 1, true)]
  [TestCase(1, false)]
  public void InvalidTailFormatIsRejectedBeforeRecovery(int version, bool validChecksum)
  {
    using var context = new Context(Tail.LocalFile, nameof(InvalidTailFormatIsRejectedBeforeRecovery) + version + validChecksum);
    using (var stream = context.Open())
    {
      stream.Write(Content(100));
      stream.SyncToDisk();
    }
    var path = context.FilePath + ".tail";
    var bytes = context.Provider.ReadAllBytes(path);
    var descriptor = bytes.AsSpan(bytes.Length - WalTailFormat.DescriptorSize);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(descriptor[8..], version);
    var checksum = unchecked((uint)System.IO.Hashing.XxHash3.HashToUInt64(descriptor[..12]));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], validChecksum ? checksum : checksum ^ 1);
    using (var file = context.Provider.CreateFileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
      file.Write(bytes);

    if (version > WalTailFormat.CurrentVersion && validChecksum)
      Assert.Throws<NotSupportedException>(() => context.Open());
    else
      Assert.Throws<InvalidDataException>(() => context.Open());
    // A failed constructor must release both files and leave the format intact.
    using var main = context.Provider.CreateFileStream(context.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
    Assert.That(context.Provider.ReadAllBytes(path), Is.EqualTo(bytes));
  }

  [Test]
  public void ExplicitSyncPreservesTheReadPosition([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(ExplicitSyncPreservesTheReadPosition));
    using var stream = context.Open();
    var content = Content(3 * BlockSize + 100);
    stream.Write(content);
    const int position = BlockSize + 17;
    stream.Seek(position, SeekOrigin.Begin);
    stream.SyncToDisk();
    Assert.That(stream.Position, Is.EqualTo(position));
    var remaining = new byte[content.Length - position];
    stream.ReadExactly(remaining);
    Assert.That(remaining, Is.EqualTo(content[position..]));
  }

  [Test]
  public void TailWriterJobSyncsTheWalToTheDisk()
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    using var stream = new CompressedFileStream(
        SilentTestLogger.Instance, provider, "data/sync", BlockSize, true, 1, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    var syncCount = provider.DiskSyncCount;
    stream.Write(Content(10));
    Assert.That(
        SpinWait.SpinUntil(() => provider.DiskSyncCount > syncCount, TimeSpan.FromSeconds(10)),
        Is.True);
  }

  [Test]
  public void TailWriterStopsWhenClosedBeforeItStarts()
  {
    var provider = new InMemoryFileStreamProvider();
    // Closing right after construction runs before the tail writer
    // thread starts in most iterations.
    var tailWriters = new List<Task>();
    for (var i = 0; i < 20; ++i)
    {
      var stream = new CompressedFileStream(
          SilentTestLogger.Instance, provider, $"tailWriter{i}", BlockSize, true, 1, CompressionMethod.None, 0);
      tailWriters.Add(stream.TailWriterTask);
      stream.Dispose();
    }
    Assert.That(Task.WaitAll(tailWriters.ToArray(), TimeSpan.FromSeconds(10)), Is.True);
  }

  [TestCase(false)]
  [TestCase(true)]
  public void FailedDiskSyncKeepsFailingLaterSyncs(bool backgroundFlush)
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    using var stream = new CompressedFileStream(
        SilentTestLogger.Instance, provider, "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    stream.Write(Content(10));
    provider.FailDiskSync = _ => true;
    Action sync = backgroundFlush ? stream.WriteTail : stream.SyncToDisk;
    Assert.That(sync, Throws.Exception);
    provider.FailDiskSync = null;
    stream.Write(Content(10));
    Assert.That(sync, Throws.Exception);
    provider.FailDiskSync = _ => false;
  }

  [Test]
  public void ConcurrentSyncsDoNotDisturbWrites([Values] Tail tail)
  {
    using var context = new Context(tail, nameof(ConcurrentSyncsDoNotDisturbWrites));
    var content = Content(20 * BlockSize);
    using (var stream = context.Open())
    {
      var isDone = false;
      var syncs = Task.Run(() =>
      {
        while (!Volatile.Read(ref isDone))
          stream.SyncToDisk();
      });
      for (var position = 0; position < content.Length; position += 128)
        stream.Write(content.AsSpan(position, 128));
      Volatile.Write(ref isDone, true);
      syncs.Wait();
    }
    using (var stream = context.Open())
      Assert.That(ReadAll(stream), Is.EqualTo(content));
  }
}
