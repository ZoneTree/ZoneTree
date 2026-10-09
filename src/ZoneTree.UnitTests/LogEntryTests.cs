using System.IO.Hashing;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class LogEntryTests
{
  [TestCase(0, 0)]
  [TestCase(5, 8)]
  [TestCase(128, 4096)]
  public void EqualContentsHaveEqualHashCodesAcrossDifferentBuffers(int keyLength, int valueLength)
  {
    var first = CreateEntry(keyLength, valueLength);
    first.Checksum = 42;
    var second = first;
    second.Key = first.Key.ToArray();
    second.Value = first.Value.ToArray();

    Assert.That(first.Equals(second), Is.True);
    Assert.That(first.Equals((object)second), Is.True);
    Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
    Assert.That(new HashSet<LogEntry> { first }.Contains(second), Is.True);
    Assert.That(new Dictionary<LogEntry, int> { [first] = 1 }[second], Is.EqualTo(1));
  }

  [TestCase(-1, 0)]
  [TestCase(0, -1)]
  [TestCase(int.MinValue, int.MaxValue)]
  public void NegativePayloadLengthsAreRejectedBeforeReadingPayload(int keyLength, int valueLength)
  {
    using var stream = new MemoryStream(CreateHeader(keyLength, valueLength));
    using var reader = new BinaryReader(stream);
    var entry = CreateEntry(5, 8);
    var original = entry;

    Assert.Throws<InvalidDataException>(() => LogEntry.ReadLogEntry(reader, ref entry));
    Assert.That(stream.Position, Is.EqualTo(16));
    Assert.That(entry, Is.EqualTo(original));
  }

  [TestCase(int.MaxValue, 0)]
  [TestCase(0, int.MaxValue)]
  [TestCase(int.MaxValue, int.MaxValue)]
  [TestCase(0, 0)]
  public void RecordMustFitRemainingStreamBeforePayloadAllocation(int keyLength, int valueLength)
  {
    using var stream = new MemoryStream(CreateHeader(keyLength, valueLength));
    using var reader = new BinaryReader(stream);
    var entry = CreateEntry(5, 8);
    var original = entry;

    Assert.Throws<EndOfStreamException>(() => LogEntry.ReadLogEntry(reader, ref entry));
    Assert.That(stream.Position, Is.EqualTo(16));
    Assert.That(entry, Is.EqualTo(original));
  }

  [TestCase(16)]
  [TestCase(18)]
  [TestCase(24)]
  [TestCase(28)]
  public void TruncatedPayloadIsAnIncompleteRecord(int truncatedLength)
  {
    var entry = CreateEntry(5, 8);
    using var stream = new MemoryStream(SerializeRecord(entry));
    stream.SetLength(truncatedLength);
    using var reader = new BinaryReader(stream);

    Assert.Throws<EndOfStreamException>(() => LogEntry.ReadLogEntry(reader, ref entry));
  }

  [TestCase(0)]
  [TestCase(8)]
  [TestCase(4096)]
  public void NonSeekableReaderHandlesShortReads(int valueLength)
  {
    var expected = CreateEntry(5, valueLength);
    expected.Checksum = expected.CreateChecksum();
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
    LogEntry.AppendLogEntry(writer, expected.Key, expected.Value, expected.OpIndex);
    stream.Position = 0;
    using var reader = new BinaryReader(new NonSeekableReadStream(stream));
    LogEntry actual = default;

    LogEntry.ReadLogEntry(reader, ref actual);

    Assert.That(actual, Is.EqualTo(expected));
    Assert.That(actual.ValidateChecksum(), Is.True);
  }

  [TestCase(int.MaxValue, 0)]
  [TestCase(0, int.MaxValue)]
  public void NonSeekableReaderDoesNotAllocateFromUntrustedLength(int keyLength, int valueLength)
  {
    using var source = new MemoryStream(CreateHeader(keyLength, valueLength));
    using var reader = new BinaryReader(new NonSeekableReadStream(source));
    LogEntry entry = default;
    var before = GC.GetAllocatedBytesForCurrentThread();
    try
    {
      LogEntry.ReadLogEntry(reader, ref entry);
      Assert.Fail("Expected an incomplete record.");
    }
    catch (EndOfStreamException)
    {
      var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
      Assert.That(allocated, Is.LessThan(64 * 1024));
    }
  }

  [TestCase(0, 0)]
  [TestCase(0, 1)]
  [TestCase(1, 0)]
  [TestCase(13, 99)]
  [TestCase(13, 100)]
  [TestCase(13, 211)]
  [TestCase(13, 212)]
  [TestCase(13, 227)]
  [TestCase(13, 228)]
  [TestCase(0, 4096)]
  [TestCase(4096, 0)]
  [TestCase(1024, 4096)]
  public void ChecksumMatchesXxh3OfSerializedRecord(int keyLength, int valueLength)
  {
    var entry = CreateEntry(keyLength, valueLength);
    var bytes = SerializeRecord(entry);

    Assert.That(entry.CreateChecksum(), Is.EqualTo(unchecked((uint)XxHash3.HashToUInt64(bytes))));
  }

  [Test]
  public void ConsecutiveRecordsRoundTripWith32BitChecksums()
  {
    var entries = new[] { CreateEntry(5, 8), CreateEntry(128, 4096), CreateEntry(0, 0) };
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
    foreach (var entry in entries)
      LogEntry.AppendLogEntry(writer, entry.Key, entry.Value, entry.OpIndex);

    Assert.That(stream.Length, Is.EqualTo(entries.Sum(x => 20L + x.KeyLength + x.ValueLength)));
    stream.Position = 0;
    using var reader = new BinaryReader(stream);
    foreach (var entry in entries)
    {
      var expected = entry;
      expected.Checksum = unchecked((uint)XxHash3.HashToUInt64(SerializeRecord(expected)));
      LogEntry actual = default;
      LogEntry.ReadLogEntry(reader, ref actual);
      Assert.That(actual, Is.EqualTo(expected));
      Assert.That(actual.ValidateChecksum(), Is.True);
    }
    Assert.That(stream.Position, Is.EqualTo(stream.Length));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  [TestCase(5)]
  public void ChecksumDetectsCorruptionInEveryRecordField(int field)
  {
    var entry = CreateEntry(128, 4096);
    entry.Checksum = entry.CreateChecksum();
    switch (field)
    {
      case 0: entry.OpIndex ^= 1; break;
      case 1: entry.KeyLength ^= 1; break;
      case 2: entry.ValueLength ^= 1; break;
      case 3: entry.Key.Span[0] ^= 1; break;
      case 4: entry.Value.Span[0] ^= 1; break;
      case 5: entry.Checksum ^= 1U << 31; break;
    }

    Assert.That(entry.ValidateChecksum(), Is.False);
  }

  [Test]
  public void TruncatedChecksumIsAnIncompleteRecord()
  {
    var entry = CreateEntry(5, 8);
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
    LogEntry.AppendLogEntry(writer, entry.Key, entry.Value, entry.OpIndex);
    var length = stream.Length;
    using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
    for (var missingBytes = 1; missingBytes <= sizeof(uint); ++missingBytes)
    {
      stream.SetLength(length - missingBytes);
      stream.Position = 0;
      LogEntry actual = default;
      Assert.Throws<EndOfStreamException>(() => LogEntry.ReadLogEntry(reader, ref actual));
    }
  }

  [Test]
  public void ReusedHasherResetsBetweenDifferentRecords()
  {
    var entries = new[] { CreateEntry(13, 228), CreateEntry(1024, 4096), CreateEntry(0, 0) };
    var expected = entries.Select(x => unchecked((uint)XxHash3.HashToUInt64(SerializeRecord(x)))).ToArray();
    for (var iteration = 0; iteration < 100; ++iteration)
      for (var i = 0; i < entries.Length; ++i)
        Assert.That(entries[i].CreateChecksum(), Is.EqualTo(expected[i]));
  }

  [Test]
  public void ConcurrentChecksumsDoNotShareHasherState()
  {
    var entries = Enumerable.Range(0, 32).Select(i => CreateEntry(i + 1, 1024 + i * 17)).ToArray();
    var expected = entries.Select(x => unchecked((uint)XxHash3.HashToUInt64(SerializeRecord(x)))).ToArray();

    Parallel.For(0, 10_000, i =>
    {
      var index = i % entries.Length;
      Assert.That(entries[index].CreateChecksum(), Is.EqualTo(expected[index]));
    });
  }

  [TestCase(8)]
  [TestCase(4096)]
  public void ChecksumDoesNotAllocateAfterWarmup(int valueLength)
  {
    var entry = CreateEntry(13, valueLength);
    for (var i = 0; i < 100; ++i)
      entry.CreateChecksum();

    var before = GC.GetAllocatedBytesForCurrentThread();
    uint checksum = 0;
    for (var i = 0; i < 1000; ++i)
      checksum = entry.CreateChecksum();
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    Assert.That(allocated, Is.Zero);
    Assert.That(checksum, Is.EqualTo(unchecked((uint)XxHash3.HashToUInt64(SerializeRecord(entry)))));
  }

  static LogEntry CreateEntry(int keyLength, int valueLength)
  {
    var key = Enumerable.Range(0, keyLength + 2).Select(i => (byte)(i * 17)).ToArray();
    var value = Enumerable.Range(0, valueLength + 2).Select(i => (byte)(i * 31)).ToArray();
    return new LogEntry
    {
      OpIndex = -0x0102030405060708L,
      KeyLength = keyLength,
      ValueLength = valueLength,
      Key = key.AsMemory(1, keyLength),
      Value = value.AsMemory(1, valueLength)
    };
  }

  static byte[] SerializeRecord(LogEntry entry)
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(entry.OpIndex);
    writer.Write(entry.KeyLength);
    writer.Write(entry.ValueLength);
    writer.Write(entry.Key.Span);
    writer.Write(entry.Value.Span);
    writer.Flush();
    return stream.ToArray();
  }

  static byte[] CreateHeader(int keyLength, int valueLength)
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(42L);
    writer.Write(keyLength);
    writer.Write(valueLength);
    writer.Flush();
    return stream.ToArray();
  }

  sealed class NonSeekableReadStream(Stream source) : Stream
  {
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override int Read(Span<byte> buffer) => source.Read(buffer[..Math.Min(buffer.Length, 3)]);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
      if (disposing)
        source.Dispose();
      base.Dispose(disposing);
    }
  }
}
