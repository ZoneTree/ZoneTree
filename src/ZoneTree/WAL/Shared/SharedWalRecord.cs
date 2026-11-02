using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;

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

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  internal void Write(CompressedFileStream stream)
  {
    var size = Size;
    Span<byte> bytes = stackalloc byte[size <= 512 ? (int)size : HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
    BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], (int)Kind);
    BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], Sequence);
    BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], SegmentId);
    BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], OpIndex);
    BinaryPrimitives.WriteInt32LittleEndian(bytes[32..], Key.Length);
    BinaryPrimitives.WriteInt32LittleEndian(bytes[36..], Value.Length);
    using var recordWrite = stream.BeginRecordWrite();
    if (size <= bytes.Length)
    {
      Key.Span.CopyTo(bytes[HeaderSize..]);
      Value.Span.CopyTo(bytes[(HeaderSize + Key.Length)..]);
      BinaryPrimitives.WriteUInt64LittleEndian(bytes[^sizeof(ulong)..], XxHash3.HashToUInt64(bytes[..^sizeof(ulong)]));
      stream.Write(bytes);
      return;
    }
    var checksum = Checksum(bytes, Key.Span, Value.Span);
    stream.Write(bytes);
    stream.Write(Key.Span);
    stream.Write(Value.Span);
    Span<byte> footer = stackalloc byte[sizeof(ulong)];
    BinaryPrimitives.WriteUInt64LittleEndian(footer, checksum);
    stream.Write(footer);
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
    if (BinaryPrimitives.ReadUInt64LittleEndian(footer) != Checksum(header, body, default))
      throw new InvalidDataException("Invalid shared WAL record checksum.");
    return new(kind,
        BinaryPrimitives.ReadInt64LittleEndian(header[8..]),
        BinaryPrimitives.ReadInt64LittleEndian(header[16..]),
        BinaryPrimitives.ReadInt64LittleEndian(header[24..]),
        body.AsMemory(0, keyLength), body.AsMemory(keyLength, valueLength));
  }

  static ulong Checksum(ReadOnlySpan<byte> header, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
  {
    var hasher = Hasher ??= new XxHash3();
    hasher.Reset();
    hasher.Append(header);
    hasher.Append(key);
    hasher.Append(value);
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
