using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;

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

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void AppendLogEntry(
      BinaryWriter binaryWriter,
      Memory<byte> keyBytes,
      Memory<byte> valueBytes,
      long opIndex,
      bool isLegacyFormat = false)
  {
    var entry = new LogEntry
    {
      OpIndex = opIndex,
      KeyLength = keyBytes.Length,
      ValueLength = valueBytes.Length,
      Key = keyBytes,
      Value = valueBytes
    };
    entry.Checksum = isLegacyFormat ? 0 : entry.CreateChecksum();
    binaryWriter.Write(entry.OpIndex);
    binaryWriter.Write(entry.KeyLength);
    binaryWriter.Write(entry.ValueLength);
    if (!entry.Key.IsEmpty)
      binaryWriter.Write(entry.Key.Span);
    if (!entry.Value.IsEmpty)
      binaryWriter.Write(entry.Value.Span);
    binaryWriter.Write(entry.Checksum);
    binaryWriter.Flush();
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
    var opIndex = reader.ReadInt64();
    var keyLength = reader.ReadInt32();
    var valueLength = reader.ReadInt32();
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
