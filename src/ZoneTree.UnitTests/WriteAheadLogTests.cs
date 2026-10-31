using System.Buffers.Binary;
using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions;
using ZoneTree.Exceptions.WAL;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class WriteAheadLogTests
{
  [Test]
  public void WalReaderQueriesStreamLengthOnceForMultipleRecords()
  {
    using var stream = new LengthCountingMemoryStream();
    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
    for (var i = 0; i < 32; ++i)
      LogEntry.AppendLogEntry(writer, new byte[] { 1, 2 }, new byte[] { 3 }, i);
    stream.LengthReadCount = 0;

    var result = WriteAheadLogEntryReader.ReadLogEntries<Memory<byte>, Memory<byte>, LogEntry>(
        new ConsoleLogger(),
        stream,
        true,
        true,
        LogEntry.ReadLogEntryWithStreamLength,
        (in LogEntry entry) => (entry.ValidateChecksum(), entry.Key, entry.Value, entry.OpIndex),
        false);

    Assert.That(result.Success, Is.True);
    Assert.That(result.Keys.Count, Is.EqualTo(32));
    Assert.That(stream.LengthReadCount, Is.EqualTo(1));
  }

  [Test]
  public void WalReaderPreservesValidPrefixWhenTailClaimsOversizedPayload()
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
    LogEntry.AppendLogEntry(writer, new byte[] { 1, 2 }, new byte[] { 3 }, 1);
    var tailPosition = stream.Position;
    writer.Write(2L);
    writer.Write(int.MaxValue);
    writer.Write(int.MaxValue);
    writer.Flush();

    var result = WriteAheadLogEntryReader.ReadLogEntries<Memory<byte>, Memory<byte>, LogEntry>(
        new ConsoleLogger(),
        stream,
        true,
        true,
        LogEntry.ReadLogEntryWithStreamLength,
        (in LogEntry entry) => (entry.ValidateChecksum(), entry.Key, entry.Value, entry.OpIndex),
        false);

    Assert.That(result.Success, Is.False);
    Assert.That(result.Keys.Count, Is.EqualTo(1));
    Assert.That(result.Values.Count, Is.EqualTo(1));
    Assert.That(result.Keys[0].ToArray(), Is.EqualTo(new byte[] { 1, 2 }));
    Assert.That(result.Values[0].ToArray(), Is.EqualTo(new byte[] { 3 }));
    Assert.That(result.MaximumOpIndex, Is.EqualTo(1));
    Assert.That(result.Exceptions.Count, Is.EqualTo(1));
    Assert.That(result.Exceptions[1], Is.TypeOf<IncompleteTailRecordFoundException>());
    var exception = (IncompleteTailRecordFoundException)result.Exceptions[1];
    Assert.That(exception.RecordPosition, Is.EqualTo(tailPosition));
    Assert.That(exception.RecordIndex, Is.EqualTo(1));
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public void WalReloadsRecordsWith32BitChecksums(WriteAheadLogMode mode)
  {
    const string category = "WalReloadsRecordsWith32BitChecksums";
    var provider = new InMemoryFileStreamProvider();
    var options = new WriteAheadLogOptions
    {
      WriteAheadLogMode = mode,
      CompressionBlockSize = 128
    };
    options.SyncCompressedModeOptions.EnableTailWriterJob = false;
    var serializer = new UnicodeStringSerializer();
    var keys = new[] { "", "small", "large", "tail" };
    var values = new[] { "", "value", new string('x', 4096), "last" };
    var walProvider = new WriteAheadLogProvider(new ConsoleLogger(), provider);
    walProvider.InitCategory(category);
    using (var wal = walProvider.GetOrCreateWAL(0, category, options, serializer, serializer))
    {
      for (var i = 0; i < keys.Length; ++i)
        wal.Append(keys[i], values[i], i + 1);
    }

    walProvider = new WriteAheadLogProvider(new ConsoleLogger(), provider);
    using var reopened = walProvider.GetOrCreateWAL(0, category, options, serializer, serializer);
    var result = reopened.ReadLogEntries(true, true, false);

    Assert.That(result.Success, Is.True);
    Assert.That(result.Exceptions, Is.Empty);
    Assert.That(result.Keys, Is.EqualTo(keys));
    Assert.That(result.Values, Is.EqualTo(values));
    Assert.That(result.MaximumOpIndex, Is.EqualTo(keys.Length));
  }

  [Test]
  public void WalBasicTest()
  {
    var filePath = "./WalBasicTest.wal";
    if (File.Exists(filePath))
      File.Delete(filePath);
    var serializer = new UnicodeStringSerializer();
    var wal = new SyncFileSystemWriteAheadLog<string, string>(
        new ConsoleLogger(),
        new LocalFileStreamProvider(),
        serializer, serializer, filePath);
    var len = 1;
    for (var i = 0; i < len; ++i)
    {
      wal.Append("key" + i, "value" + i, i);
    }
    var result = wal.ReadLogEntries(false, false, true);
    Assert.That(result.Success, Is.True);
    Assert.That(result.Exceptions, Is.Empty);
    Assert.That(result.Keys.Count, Is.EqualTo(len));
    Assert.That(result.Values.Count, Is.EqualTo(len));
    for (var i = 0; i < len; ++i)
    {
      Assert.That(result.Keys[i], Is.EqualTo("key" + i));
      Assert.That(result.Values[i], Is.EqualTo("value" + i));
    }
    Assert.That(result.MaximumOpIndex, Is.EqualTo(len - 1));
    wal.Drop();
  }

  [Test]
  public void WalReportsMaximumOpIndexWithoutSorting()
  {
    var filePath = "./WalReportsMaximumOpIndexWithoutSorting.wal";
    if (File.Exists(filePath))
      File.Delete(filePath);
    var serializer = new UnicodeStringSerializer();
    var wal = new SyncFileSystemWriteAheadLog<string, string>(
        new ConsoleLogger(),
        new LocalFileStreamProvider(),
        serializer, serializer, filePath);

    wal.Append("key0", "value0", 5);
    wal.Append("key1", "value1", 2);
    wal.Append("key2", "value2", 7);

    var result = wal.ReadLogEntries(false, false, false);

    Assert.That(result.Success, Is.True);
    Assert.That(result.Keys, Is.EqualTo(new[] { "key0", "key1", "key2" }));
    Assert.That(result.Values, Is.EqualTo(new[] { "value0", "value1", "value2" }));
    Assert.That(result.MaximumOpIndex, Is.EqualTo(7));
    wal.Drop();
  }

  [Test]
  public void TestWriteAheadLogCorruptionException()
  {
    Assert.DoesNotThrow(() => { new WriteAheadLogCorruptionException(0, null); });
    Assert.DoesNotThrow(() =>
    {
      new WriteAheadLogCorruptionException(0,
        new Dictionary<int, Exception> { });
    });
  }

  [Test]
  public void CompressedFileStreamIgnoresIncompleteMainBlockPayload()
  {
    var provider = new InMemoryFileStreamProvider();
    var filePath = "CompressedFileStreamIgnoresIncompleteMainBlockPayload.wal";
    const int blockSize = 16;
    var block0 = Enumerable.Range(0, blockSize).Select(x => (byte)x).ToArray();
    var block1 = Enumerable.Range(blockSize, blockSize).Select(x => (byte)x).ToArray();

    WriteFile(provider, filePath, CreateMainFileWithIncompleteSecondBlock(block0, block1, 4));
    WriteFile(provider, filePath + ".tail", CreateTailFile(1, block1));

    var expected = block0.Concat(block1).ToArray();
    var actual = new byte[expected.Length];
    long streamLength;
    int readLength;
    using (var stream = new CompressedFileStream(
        new ConsoleLogger(),
        provider,
        filePath,
        blockSize,
        false,
        0,
        CompressionMethod.None,
        0))
    {
      stream.Seek(0, SeekOrigin.Begin);
      readLength = stream.Read(actual, 0, actual.Length);
      streamLength = stream.Length;
    }

    Assert.That(streamLength, Is.EqualTo(expected.Length));
    Assert.That(readLength, Is.EqualTo(expected.Length));
    Assert.That(actual, Is.EqualTo(expected));
    Assert.That(
        provider.ReadAllBytes(filePath).Length,
        Is.EqualTo(sizeof(byte) + 3 * sizeof(int) + block0.Length));
  }

  [Test]
  public void CompressedFileStreamSkipAcrossFullBlockIntoShortTail()
  {
    var provider = new InMemoryFileStreamProvider();
    var filePath = "CompressedFileStreamSkipAcrossFullBlockIntoShortTail.wal";
    const int blockSize = 16;
    var block0 = Enumerable.Range(0, blockSize).Select(x => (byte)x).ToArray();
    var tailBlock = Enumerable.Range(blockSize, 8).Select(x => (byte)x).ToArray();

    WriteFile(provider, filePath, CreateMainFileWithSingleBlock(block0));
    WriteFile(provider, filePath + ".tail", CreateTailFile(1, tailBlock));

    var buffer = new byte[1];
    int readLength;
    long seekPosition;
    using (var stream = new CompressedFileStream(
        new ConsoleLogger(),
        provider,
        filePath,
        blockSize,
        false,
        0,
        CompressionMethod.None,
        0))
    {
      stream.Seek(blockSize - 2, SeekOrigin.Begin);
      seekPosition = stream.Seek(4, SeekOrigin.Current);
      readLength = stream.Read(buffer, 0, buffer.Length);
    }

    Assert.That(seekPosition, Is.EqualTo(blockSize + 2));
    Assert.That(readLength, Is.EqualTo(1));
    Assert.That(buffer[0], Is.EqualTo(tailBlock[2]));
  }

  [Test]
  public void CompressedFileStreamTailWriterStopsWhenClosedBeforeItStarts()
  {
    var provider = new InMemoryFileStreamProvider();
    // Closing right after construction runs before the tail writer
    // thread starts in most iterations.
    var tailWriters = new List<Task>();
    for (var i = 0; i < 20; ++i)
    {
      var stream = new CompressedFileStream(
          new ConsoleLogger(),
          provider,
          $"CompressedFileStreamTailWriterStopsWhenClosedBeforeItStarts{i}.wal",
          16,
          true,
          1,
          CompressionMethod.None,
          0);
      tailWriters.Add(stream.TailWriterTask);
      stream.Dispose();
    }

    Assert.That(Task.WaitAll(tailWriters.ToArray(), TimeSpan.FromSeconds(10)), Is.True);
  }

  [Test]
  public void CompressedFileStreamDoesNotRewriteUnchangedTailOnClose()
  {
    var dataPath = "data/CompressedFileStreamDoesNotRewriteUnchangedTailOnClose";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    Directory.CreateDirectory(dataPath);
    var provider = new LocalFileStreamProvider();
    var filePath = Path.Combine(dataPath, "stream.wal");
    var tailPath = filePath + ".tail";
    const int blockSize = 32;
    // Shorter than a block so the data stays in the tail.
    var expected = Enumerable.Range(0, 10).Select(x => (byte)x).ToArray();

    CompressedFileStream Open() => new(
        new ConsoleLogger(),
        provider,
        filePath,
        blockSize,
        false,
        0,
        CompressionMethod.None,
        0);

    using (var stream = Open())
      stream.Write(expected, 0, expected.Length);

    var unchangedSince = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(tailPath, unchangedSince);

    using (Open())
    {
    }

    Assert.That(File.GetLastWriteTimeUtc(tailPath), Is.EqualTo(unchangedSince));
    var actual = new byte[expected.Length];
    int readLength;
    using (var stream = Open())
      readLength = stream.Read(actual, 0, actual.Length);
    Assert.That(readLength, Is.EqualTo(expected.Length));
    Assert.That(actual, Is.EqualTo(expected));
  }

  // A loaded tail is not rewritten on close. Truncations after loading must
  // still persist the tail, or the next open reads stale tail bytes.

  [Test]
  public void CompressedFileStreamPersistsTailAfterTruncatingIntoMainFile()
  {
    // Two committed blocks and 8 tail bytes.
    AssertReopenedStreamMatchesState(
        "CompressedFileStreamPersistsTailAfterTruncatingIntoMainFile.wal",
        40,
        stream => stream.SetLength(20));
  }

  // Two committed 16-byte blocks and 8 tail bytes: cut inside a block,
  // at block boundaries, at the end of the main file and inside the tail.
  [TestCase(1)]
  [TestCase(15)]
  [TestCase(16)]
  [TestCase(20)]
  [TestCase(31)]
  [TestCase(32)]
  [TestCase(33)]
  [TestCase(39)]
  public void CompressedFileStreamTruncationIntoMainFileKeepsRequestedLength(int truncatedLength)
  {
    var provider = new InMemoryFileStreamProvider();
    var filePath = "CompressedFileStreamTruncationIntoMainFileKeepsRequestedLength.wal";
    var data = Enumerable.Range(0, 40).Select(x => (byte)x).ToArray();

    CompressedFileStream Open() => new(
        new ConsoleLogger(),
        provider,
        filePath,
        16,
        false,
        0,
        CompressionMethod.None,
        0);

    using (var stream = Open())
      stream.Write(data, 0, data.Length);

    long lengthAfterTruncation;
    using (var stream = Open())
    {
      stream.SetLength(truncatedLength);
      lengthAfterTruncation = stream.Length;
    }

    var actual = new byte[data.Length];
    int readLength;
    using (var stream = Open())
    {
      stream.Seek(0, SeekOrigin.Begin);
      readLength = stream.Read(actual, 0, actual.Length);
    }

    Assert.That(lengthAfterTruncation, Is.EqualTo(truncatedLength));
    Assert.That(actual.Take(readLength), Is.EqualTo(data.Take(truncatedLength)));
  }

  [Test]
  public void CompressedFileStreamCanAppendAfterTruncatingWithinTail()
  {
    var provider = new InMemoryFileStreamProvider();
    var filePath = "CompressedFileStreamCanAppendAfterTruncatingWithinTail.wal";
    // All 8 bytes stay in the tail of a 16-byte block.
    var data = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray();
    var appended = new byte[] { 100, 101, 102, 103 };

    CompressedFileStream Open() => new(
        new ConsoleLogger(),
        provider,
        filePath,
        16,
        false,
        0,
        CompressionMethod.None,
        0);

    using (var stream = Open())
      stream.Write(data, 0, data.Length);

    using (var stream = Open())
    {
      stream.Seek(0, SeekOrigin.End);
      stream.SetLength(4);
      // A WAL appends right after truncating an incomplete record.
      stream.Write(appended, 0, appended.Length);
    }

    var actual = new byte[data.Length];
    int readLength;
    using (var stream = Open())
    {
      stream.Seek(0, SeekOrigin.Begin);
      readLength = stream.Read(actual, 0, actual.Length);
    }

    Assert.That(actual.Take(readLength), Is.EqualTo(data.Take(4).Concat(appended)));
  }

  // A tail loaded from disk or trimmed by a truncation is sized to its content.
  // Appending must grow it to the block size instead of committing it as a
  // short or empty block. Each case runs with every compression method.

  [Test]
  public void CompressedFileStreamAppendAfterReopenFillsLoadedTail(
      [Values] CompressionMethod compressionMethod)
  {
    var provider = new InMemoryFileStreamProvider();
    var filePath = $"CompressedFileStreamAppendAfterReopenFillsLoadedTail.{compressionMethod}.wal";
    var data = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();

    using (var stream = OpenCompressedFileStream(provider, filePath, compressionMethod))
      stream.Write(data, 0, 8);
    using (var stream = OpenCompressedFileStream(provider, filePath, compressionMethod))
      stream.Write(data, 8, 8);

    Assert.That(GetCommittedBlockLengths(provider, filePath), Is.EqualTo(new[] { 16 }));
    using (var stream = OpenCompressedFileStream(provider, filePath, compressionMethod))
      Assert.That(ReadStream(stream), Is.EqualTo(data));
  }

  [Test]
  public void CompressedFileStreamAppendAfterReopenWithEmptyTailCommitsNoEmptyBlock(
      [Values] CompressionMethod compressionMethod)
  {
    var provider = new InMemoryFileStreamProvider();
    var filePath = $"CompressedFileStreamAppendAfterReopenWithEmptyTailCommitsNoEmptyBlock.{compressionMethod}.wal";
    var data = Enumerable.Range(0, 36).Select(x => (byte)x).ToArray();

    // A full block leaves an empty tail on disk.
    using (var stream = OpenCompressedFileStream(provider, filePath, compressionMethod))
      stream.Write(data, 0, 16);
    using (var stream = OpenCompressedFileStream(provider, filePath, compressionMethod))
      stream.Write(data, 16, 20);

    Assert.That(GetCommittedBlockLengths(provider, filePath), Is.EqualTo(new[] { 16, 16 }));
    using (var stream = OpenCompressedFileStream(provider, filePath, compressionMethod))
      Assert.That(ReadStream(stream), Is.EqualTo(data));
  }

  [Test]
  public void CompressedFileStreamAppendAfterTruncationCommitsNoEmptyBlock(
      [Values] CompressionMethod compressionMethod)
  {
    var provider = new InMemoryFileStreamProvider();
    var filePath = $"CompressedFileStreamAppendAfterTruncationCommitsNoEmptyBlock.{compressionMethod}.wal";
    var data = Enumerable.Range(0, 40).Select(x => (byte)x).ToArray();
    var appended = Enumerable.Range(100, 20).Select(x => (byte)x).ToArray();

    using (var stream = OpenCompressedFileStream(provider, filePath, compressionMethod))
      stream.Write(data, 0, data.Length);
    using (var stream = OpenCompressedFileStream(provider, filePath, compressionMethod))
    {
      // Cut at a block boundary: the tail is trimmed to nothing.
      stream.SetLength(16);
      stream.Write(appended, 0, appended.Length);
    }

    Assert.That(GetCommittedBlockLengths(provider, filePath), Is.EqualTo(new[] { 16, 16 }));
    using (var stream = OpenCompressedFileStream(provider, filePath, compressionMethod))
      Assert.That(ReadStream(stream), Is.EqualTo(data.Take(16).Concat(appended)));
  }

  static CompressedFileStream OpenCompressedFileStream(
      InMemoryFileStreamProvider provider,
      string filePath,
      CompressionMethod compressionMethod)
  {
    return new CompressedFileStream(
        new ConsoleLogger(),
        provider,
        filePath,
        16,
        false,
        0,
        compressionMethod,
        0);
  }

  static byte[] ReadStream(CompressedFileStream stream)
  {
    var bytes = new byte[stream.Length];
    stream.Seek(0, SeekOrigin.Begin);
    var readLength = stream.Read(bytes, 0, bytes.Length);
    return bytes.AsSpan(0, readLength).ToArray();
  }

  /// <summary>
  /// Reads the decompressed length of every committed block in the main file:
  /// one metadata byte, then block index, compressed length, decompressed
  /// length and the compressed payload for each block.
  /// </summary>
  static int[] GetCommittedBlockLengths(InMemoryFileStreamProvider provider, string filePath)
  {
    var bytes = provider.ReadAllBytes(filePath);
    var lengths = new List<int>();
    var offset = 1;
    while (offset < bytes.Length)
    {
      var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + sizeof(int)));
      lengths.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 2 * sizeof(int))));
      offset += 3 * sizeof(int) + compressedLength;
    }
    return lengths.ToArray();
  }

  /// <summary>
  /// Writes the initial bytes, reopens the stream, applies the change
  /// and expects a later reopen to read exactly the content
  /// the stream had before it was closed.
  /// </summary>
  static void AssertReopenedStreamMatchesState(
      string filePath,
      int initialLength,
      Action<CompressedFileStream> change)
  {
    var provider = new InMemoryFileStreamProvider();
    const int blockSize = 16;
    var data = Enumerable.Range(0, initialLength).Select(x => (byte)x).ToArray();

    CompressedFileStream Open() => new(
        new ConsoleLogger(),
        provider,
        filePath,
        blockSize,
        false,
        0,
        CompressionMethod.None,
        0);

    static byte[] ReadAll(CompressedFileStream stream)
    {
      var bytes = new byte[stream.Length];
      stream.Seek(0, SeekOrigin.Begin);
      var readLength = stream.Read(bytes, 0, bytes.Length);
      return bytes.AsSpan(0, readLength).ToArray();
    }

    using (var stream = Open())
      stream.Write(data, 0, data.Length);

    byte[] expected;
    using (var stream = Open())
    {
      change(stream);
      expected = ReadAll(stream);
    }

    byte[] actual;
    using (var stream = Open())
      actual = ReadAll(stream);

    Assert.That(actual, Is.EqualTo(expected));
  }

  static void WriteFile(
      InMemoryFileStreamProvider provider,
      string path,
      byte[] bytes)
  {
    using var stream = provider.CreateFileStream(
        path,
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);
    stream.Write(bytes, 0, bytes.Length);
  }

  static byte[] CreateMainFileWithSingleBlock(byte[] block)
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((byte)CompressionMethod.None);
    WriteCompressedBlock(writer, 0, block, block.Length);
    writer.Flush();
    return stream.ToArray();
  }

  static byte[] CreateMainFileWithIncompleteSecondBlock(
      byte[] block0,
      byte[] block1,
      int secondBlockBytesWritten)
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((byte)CompressionMethod.None);
    WriteCompressedBlock(writer, 0, block0, block0.Length);
    WriteCompressedBlock(writer, 1, block1, secondBlockBytesWritten);
    writer.Flush();
    return stream.ToArray();
  }

  static byte[] CreateTailFile(int blockIndex, byte[] block)
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(blockIndex);
    writer.Write(block.Length);
    writer.Write(block);
    writer.Flush();
    return stream.ToArray();
  }

  static void WriteCompressedBlock(
      BinaryWriter writer,
      int blockIndex,
      byte[] block,
      int bytesToWrite)
  {
    writer.Write(blockIndex);
    writer.Write(block.Length);
    writer.Write(block.Length);
    writer.Write(block, 0, bytesToWrite);
  }

  sealed class LengthCountingMemoryStream : MemoryStream
  {
    public int LengthReadCount;

    public override long Length
    {
      get
      {
        ++LengthReadCount;
        return base.Length;
      }
    }
  }
}
