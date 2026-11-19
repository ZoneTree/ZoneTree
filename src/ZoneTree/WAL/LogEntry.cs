using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

public struct LogEntry : IEquatable<LogEntry>
{
  const int HeaderLength = sizeof(long) + 2 * sizeof(int);

  const int StackBufferLength = 256;

  [ThreadStatic]
  static XxHash3 ThreadHasher;

  public long OpIndex;

  public int KeyLength;

  public int ValueLength;

  public Memory<byte> Key;

  public Memory<byte> Value;

  public uint Checksum;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public uint CreateChecksum()
  {
    var key = Key.Span;
    var value = Value.Span;
    var length = HeaderLength + (long)key.Length + value.Length;
    Span<byte> buffer = stackalloc byte[length <= StackBufferLength ? (int)length : HeaderLength];
    BinaryPrimitives.WriteInt64LittleEndian(buffer, OpIndex);
    BinaryPrimitives.WriteInt32LittleEndian(buffer[sizeof(long)..], KeyLength);
    BinaryPrimitives.WriteInt32LittleEndian(buffer[(sizeof(long) + sizeof(int))..], ValueLength);

    // Hash small records in one call without allocating a hasher.
    if (length <= StackBufferLength)
    {
      key.CopyTo(buffer[HeaderLength..]);
      value.CopyTo(buffer[(HeaderLength + key.Length)..]);
      return unchecked((uint)XxHash3.HashToUInt64(buffer));
    }

    // Reuse synchronous, thread-local state without combining large payloads.
    var hasher = ThreadHasher ??= new XxHash3();
    hasher.Reset();
    hasher.Append(buffer);
    hasher.Append(key);
    hasher.Append(value);
    return unchecked((uint)hasher.GetCurrentHashAsUInt64());
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool ValidateChecksum()
  {
    return CreateChecksum() == Checksum;
  }

  /// <summary>
  /// Serializes the entry's record, its header, key, value and checksum, into the calling thread's buffer, so the
  /// record is written in one write without allocating. The record is valid until the thread serializes its next
  /// record.
  /// </summary>
  public static ReadOnlySpan<byte> SerializeLogEntry<TKey, TValue>(
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer,
      in TKey key,
      in TValue value,
      long opIndex,
      bool isLegacyFormat = false)
  {
    var record = WalEntrySerializer.SerializeRecord(
        keySerializer, valueSerializer, in key, in value, HeaderLength, sizeof(uint),
        out var keyLength, out var valueLength);
    BinaryPrimitives.WriteInt64LittleEndian(record, opIndex);
    BinaryPrimitives.WriteInt32LittleEndian(record[sizeof(long)..], keyLength);
    BinaryPrimitives.WriteInt32LittleEndian(record[(sizeof(long) + sizeof(int))..], valueLength);
    var checksum = isLegacyFormat
        ? 0
        : unchecked((uint)XxHash3.HashToUInt64(record[..^sizeof(uint)]));
    BinaryPrimitives.WriteUInt32LittleEndian(record[^sizeof(uint)..], checksum);
    return record;
  }

  /// <summary>
  /// Writes the entry's record in one write and flushes the writer.
  /// </summary>
  public static void AppendLogEntry<TKey, TValue>(
      BinaryWriter binaryWriter,
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer,
      in TKey key,
      in TValue value,
      long opIndex,
      bool isLegacyFormat = false)
  {
    binaryWriter.Write(SerializeLogEntry(keySerializer, valueSerializer, in key, in value, opIndex, isLegacyFormat));
    binaryWriter.Flush();
  }

  /// <summary>
  /// The length of the shortest record.
  /// </summary>
  internal const int MinimumRecordLength = HeaderLength + sizeof(uint);

  /// <summary>
  /// Reads the record length from the bytes of a record header.
  /// </summary>
  /// <returns>The record length, or -1 if the header cannot belong to a record.</returns>
  internal static long ReadRecordLength(ReadOnlySpan<byte> header)
  {
    var keyLength = BinaryPrimitives.ReadInt32LittleEndian(header[sizeof(long)..]);
    var valueLength = BinaryPrimitives.ReadInt32LittleEndian(header[(sizeof(long) + sizeof(int))..]);
    if (keyLength < 0 || valueLength < 0)
      return -1;
    return MinimumRecordLength + (long)keyLength + valueLength;
  }

  /// <summary>
  /// Validates the checksum of the bytes of a whole record.
  /// </summary>
  internal static bool HasValidChecksum(ReadOnlySpan<byte> record)
  {
    return unchecked((uint)XxHash3.HashToUInt64(record[..^sizeof(uint)])) ==
        BinaryPrimitives.ReadUInt32LittleEndian(record[^sizeof(uint)..]);
  }

  /// <summary>
  /// Writes the checksum of the bytes of a whole record.
  /// </summary>
  internal static void WriteChecksum(Span<byte> record)
  {
    BinaryPrimitives.WriteUInt32LittleEndian(
        record[^sizeof(uint)..],
        unchecked((uint)XxHash3.HashToUInt64(record[..^sizeof(uint)])));
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void ReadLogEntry(BinaryReader reader, ref LogEntry entry)
  {
    var stream = reader.BaseStream;
    ReadLogEntryWithStreamLength(reader, ref entry, stream.CanSeek ? stream.Length : -1);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void ReadLogEntryWithStreamLength(BinaryReader reader, ref LogEntry entry, long streamLength)
  {
    Span<byte> header = stackalloc byte[HeaderLength];
    ReadHeader(reader, header);
    var opIndex = BinaryPrimitives.ReadInt64LittleEndian(header);
    var keyLength = BinaryPrimitives.ReadInt32LittleEndian(header[sizeof(long)..]);
    var valueLength = BinaryPrimitives.ReadInt32LittleEndian(header[(sizeof(long) + sizeof(int))..]);
    if (keyLength < 0 || valueLength < 0)
      throw new InvalidDataException("WAL record lengths cannot be negative.");

    var stream = reader.BaseStream;
    var requiredLength = (long)keyLength + valueLength + sizeof(uint);
    if (streamLength >= 0 && requiredLength > streamLength - stream.Position)
      throw new EndOfStreamException("Incomplete WAL record payload or checksum.");

    var key = ReadPayload(reader, keyLength);
    var value = ReadPayload(reader, valueLength);
    var checksum = reader.ReadUInt32();
    entry = new LogEntry
    {
      OpIndex = opIndex,
      KeyLength = keyLength,
      ValueLength = valueLength,
      Key = key,
      Value = value,
      Checksum = checksum
    };
  }

  /// <summary>
  /// Reads the record header in one call. A missing byte means an incomplete
  /// record, reported as EndOfStreamException like the single field reads.
  /// </summary>
  static void ReadHeader(BinaryReader reader, Span<byte> buffer)
  {
    while (buffer.Length > 0)
    {
      var read = reader.Read(buffer);
      if (read == 0)
        throw new EndOfStreamException("Incomplete WAL record header.");
      buffer = buffer[read..];
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static byte[] ReadPayload(BinaryReader reader, int length)
  {
    if (!reader.BaseStream.CanSeek)
      return ReadNonSeekablePayload(reader, length);

    var bytes = reader.ReadBytes(length);
    if (bytes.Length != length)
      throw new EndOfStreamException("Incomplete WAL record payload.");
    return bytes;
  }

  static byte[] ReadNonSeekablePayload(BinaryReader reader, int length)
  {
    // Without a remaining length, grow only as bytes actually arrive.
    if (length == 0)
      return [];
    using var buffer = new MemoryStream();
    Span<byte> chunk = stackalloc byte[Math.Min(length, 4096)];
    while (length > 0)
    {
      var read = reader.Read(chunk[..Math.Min(length, chunk.Length)]);
      if (read == 0)
        throw new EndOfStreamException("Incomplete WAL record payload.");
      buffer.Write(chunk[..read]);
      length -= read;
    }
    return buffer.ToArray();
  }

  public override bool Equals(object obj)
  {
    return obj is LogEntry entry && Equals(entry);
  }

  public bool Equals(LogEntry other)
  {
    return OpIndex == other.OpIndex &&
           KeyLength == other.KeyLength &&
           ValueLength == other.ValueLength &&
           Key.Span.SequenceEqual(other.Key.Span) &&
           Value.Span.SequenceEqual(other.Value.Span) &&
           Checksum == other.Checksum;
  }

  public override int GetHashCode()
  {
    // Used by hash-based collections such as Dictionary<LogEntry, ...> and HashSet<LogEntry>.
    // WAL writes and replay do not call this method; hash payload contents to match Equals.
    return HashCode.Combine(OpIndex, KeyLength, ValueLength, XxHash3.HashToUInt64(Key.Span), XxHash3.HashToUInt64(Value.Span), Checksum);
  }

  public static bool operator ==(LogEntry left, LogEntry right)
  {
    return left.Equals(right);
  }

  public static bool operator !=(LogEntry left, LogEntry right)
  {
    return !(left == right);
  }
}
