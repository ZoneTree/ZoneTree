using System.Buffers.Binary;
using System.IO.Hashing;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

internal enum SharedWalRecordKind
{
  ChunkHeader,
  Data,
  BeginReplacement,
  EndReplacement,
  SealChunk,
  AbortReplacement,
}

/// <summary>
/// A checksummed journal envelope. Sequence numbers order records across
/// logical segments; operation indexes retain the producer's separate order.
/// Chunk headers and seals name boundaries without consuming a sequence.
/// </summary>
internal readonly record struct SharedWalRecord(
    SharedWalRecordKind Kind, long Sequence, long SegmentId, long OpIndex,
    Memory<byte> Key, Memory<byte> Value)
{
  const uint Magic = 0x314A545A; // ZTJ1

  const int HeaderSize = 40;

  internal const int MinimumSize = HeaderSize + sizeof(ulong);

  [ThreadStatic]
  static XxHash3 Hasher;

  internal long Size => MinimumSize + (long)Key.Length + Value.Length;

  // A record is written from this struct only as a control record: a chunk header, a seal or a replacement marker,
  // whose value is at most a count. Data records are serialized with SerializeData.
  const int MaxControlRecordSize = MinimumSize + sizeof(long);

  /// <summary>
  /// Writes the control record in one write. It is built on the stack, and not in the calling thread's buffer,
  /// because the buffer can hold a data record that is written after it, such as when a seal precedes the data
  /// record that does not fit the chunk.
  /// </summary>
  internal void Write(CompressedFileStream stream)
  {
    if (Size > MaxControlRecordSize)
      throw new InvalidOperationException("Only control records are written from a record. Data records are serialized.");
    Span<byte> record = stackalloc byte[MaxControlRecordSize];
    record = record[..(int)Size];
    WriteHeader(record, Kind, Sequence, SegmentId, OpIndex, Key.Length, Value.Length);
    Key.Span.CopyTo(record[HeaderSize..]);
    Value.Span.CopyTo(record[(HeaderSize + Key.Length)..]);
    WriteChecksum(record);
    WriteRecord(stream, record);
  }

  /// <summary>
  /// Serializes a data record of the entry, its header, key, value and checksum, into the calling thread's buffer,
  /// and returns its bytes. They are valid until the thread serializes its next record.
  /// </summary>
  internal static ReadOnlySpan<byte> SerializeData<TKey, TValue>(
      ISerializer<TKey> keySerializer, ISerializer<TValue> valueSerializer, in TKey key, in TValue value,
      long sequence, long segmentId, long opIndex)
  {
    var record = WalEntrySerializer.SerializeRecord(
        keySerializer, valueSerializer, in key, in value, HeaderSize, sizeof(ulong),
        out var keyLength, out var valueLength);
    WriteHeader(record, SharedWalRecordKind.Data, sequence, segmentId, opIndex, keyLength, valueLength);
    WriteChecksum(record);
    return record;
  }

  /// <summary>
  /// Writes the bytes of a whole record in one write.
  /// </summary>
  internal static void WriteRecord(CompressedFileStream stream, ReadOnlySpan<byte> record)
  {
    using var recordWrite = stream.BeginRecordWrite();
    stream.Write(record);
  }

  // Writes the checksum of the record's bytes before it, at its end. It is the checksum Read checks.
  static void WriteChecksum(Span<byte> record)
  {
    BinaryPrimitives.WriteUInt64LittleEndian(
        record[^sizeof(ulong)..], XxHash3.HashToUInt64(record[..^sizeof(ulong)]));
  }

  static void WriteHeader(
      Span<byte> bytes, SharedWalRecordKind kind, long sequence, long segmentId, long opIndex,
      int keyLength, int valueLength)
  {
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
    BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], (int)kind);
    BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], sequence);
    BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], segmentId);
    BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], opIndex);
    BinaryPrimitives.WriteInt32LittleEndian(bytes[32..], keyLength);
    BinaryPrimitives.WriteInt32LittleEndian(bytes[36..], valueLength);
  }

  internal static SharedWalRecord Read(CompressedFileStream stream)
  {
    Span<byte> header = stackalloc byte[HeaderSize];
    ReadExactly(stream, header);
    var kind = (SharedWalRecordKind)BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
    var keyLength = BinaryPrimitives.ReadInt32LittleEndian(header[32..]);
    var valueLength = BinaryPrimitives.ReadInt32LittleEndian(header[36..]);
    var bodyLength = (long)keyLength + valueLength;
    if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
        kind < SharedWalRecordKind.ChunkHeader || kind > SharedWalRecordKind.AbortReplacement ||
        keyLength < 0 || valueLength < 0 || bodyLength > int.MaxValue ||
        bodyLength + sizeof(ulong) > stream.Length - stream.Position)
      throw new InvalidDataException("Invalid shared WAL record header.");
    var body = new byte[(int)bodyLength];
    ReadExactly(stream, body);
    Span<byte> footer = stackalloc byte[sizeof(ulong)];
    ReadExactly(stream, footer);
    if (BinaryPrimitives.ReadUInt64LittleEndian(footer) != Checksum(header, body))
      throw new InvalidDataException("Invalid shared WAL record checksum.");
    return new(kind,
        BinaryPrimitives.ReadInt64LittleEndian(header[8..]),
        BinaryPrimitives.ReadInt64LittleEndian(header[16..]),
        BinaryPrimitives.ReadInt64LittleEndian(header[24..]),
        body.AsMemory(0, keyLength), body.AsMemory(keyLength, valueLength));
  }

  // The checksum of a record read as a header and a body, the one WriteChecksum writes for the whole record.
  static ulong Checksum(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
  {
    var hasher = Hasher ??= new XxHash3();
    hasher.Reset();
    hasher.Append(header);
    hasher.Append(body);
    return hasher.GetCurrentHashAsUInt64();
  }

  static void ReadExactly(CompressedFileStream stream, Span<byte> bytes)
  {
    while (bytes.Length > 0)
    {
      var count = stream.Read(bytes);
      if (count == 0)
        throw new InvalidDataException("Incomplete shared WAL record.");
      bytes = bytes[count..];
    }
  }
}
