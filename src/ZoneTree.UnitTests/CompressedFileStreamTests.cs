using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
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
          new ConsoleLogger(),
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
      stream.Write(Content(BlockSize + 300));
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
        new ConsoleLogger(), provider, "data/crash", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    for (var position = 0; position < content.Length;)
    {
      var writeLength = Math.Min(211, content.Length - position);
      stream.Write(content.AsSpan(position, writeLength));
      position += writeLength;
      var afterCrash = CopyAsProcessCrash(provider, "data/crash", "data/crash.tail");
      using var recovered = new CompressedFileStream(
          new ConsoleLogger(), afterCrash, "data/crash", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
      Assert.That(ReadAll(recovered), Is.EqualTo(content.AsSpan(0, position).ToArray()));
    }
  }

  [Test]
  public void ProcessCrashDuringTailRolloverKeepsTheCommittedBlock()
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    IFileStreamProvider afterCrash = null;
    provider.AfterSetLength = path =>
    {
      if (path.EndsWith(".tail", StringComparison.Ordinal))
        afterCrash = CopyAsProcessCrash(provider, "data/rollover", "data/rollover.tail");
    };
    // Zeros without compression look like a sector that did not persist.
    var content = new byte[BlockSize];
    using var stream = new CompressedFileStream(
        new ConsoleLogger(), provider, "data/rollover", BlockSize, false, 0, CompressionMethod.None, 0);
    stream.Write(content);
    provider.AfterSetLength = null;

    using var recovered = new CompressedFileStream(
        new ConsoleLogger(), afterCrash, "data/rollover", BlockSize, false, 0, CompressionMethod.None, 0);
    Assert.That(ReadAll(recovered), Is.EqualTo(content));
  }

  [Test]
  public void SyncToDiskSurvivesAPowerLoss()
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    var content = Content(3 * BlockSize + 100);
    using var stream = new CompressedFileStream(
        new ConsoleLogger(), provider, "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    for (var position = 0; position < content.Length;)
    {
      var writeLength = Math.Min(777, content.Length - position);
      stream.Write(content.AsSpan(position, writeLength));
      position += writeLength;
      stream.SyncToDisk();
      var afterPowerLoss = provider.SimulatePowerLoss();
      using var recovered = new CompressedFileStream(
          new ConsoleLogger(), afterPowerLoss, "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
      Assert.That(ReadAll(recovered), Is.EqualTo(content.AsSpan(0, position).ToArray()));
    }
  }

  [Test]
  public void SyncToDiskSyncsOnlyWhatChanged()
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    using var stream = new CompressedFileStream(
        new ConsoleLogger(), provider, "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    stream.Write(Content(10));
    var syncCount = provider.DiskSyncCount;
    stream.SyncToDisk();
    Assert.That(provider.DiskSyncCount - syncCount, Is.EqualTo(1));
    stream.SyncToDisk();
    Assert.That(provider.DiskSyncCount - syncCount, Is.EqualTo(1));
  }

  [Test]
  public void WriteTailSurvivesAPowerLoss()
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    var content = Content(BlockSize + 100);
    using var stream = new CompressedFileStream(
        new ConsoleLogger(), provider, "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    stream.Write(content);
    stream.WriteTail();
    using var recovered = new CompressedFileStream(
        new ConsoleLogger(), provider.SimulatePowerLoss(), "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    Assert.That(ReadAll(recovered), Is.EqualTo(content));
  }

  [Test]
  public void TailWriterJobSyncsTheTailToTheDisk()
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    using var stream = new CompressedFileStream(
        new ConsoleLogger(), provider, "data/sync", BlockSize, true, 1, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
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
          new ConsoleLogger(), provider, $"tailWriter{i}", BlockSize, true, 1, CompressionMethod.None, 0);
      tailWriters.Add(stream.TailWriterTask);
      stream.Dispose();
    }
    Assert.That(Task.WaitAll(tailWriters.ToArray(), TimeSpan.FromSeconds(10)), Is.True);
  }

  [Test]
  public void FailedDiskSyncKeepsFailingLaterSyncs()
  {
    var provider = new PowerLossFileStreamProvider();
    provider.CreateDirectory("data");
    using var stream = new CompressedFileStream(
        new ConsoleLogger(), provider, "data/sync", BlockSize, false, 0, CompressionMethod.LZ4, CompressionLevels.LZ4Fastest);
    stream.Write(Content(10));
    provider.FailDiskSync = _ => true;
    Assert.That(stream.SyncToDisk, Throws.Exception);
    provider.FailDiskSync = null;
    stream.Write(Content(10));
    Assert.That(stream.SyncToDisk, Throws.Exception);
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
