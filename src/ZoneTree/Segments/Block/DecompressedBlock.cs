using System;
using ZoneTree.Compression;
using ZoneTree.Options;

namespace ZoneTree.Segments.Block;

public sealed class DecompressedBlock
{
  public CompressionMethod CompressionMethod { get; }

  public int CompressionLevel { get; }

  public int BlockIndex { get; private set; }

  public volatile int _length;

  public int Length
  {
    get => _length;
    set => _length = value;
  }

  Memory<byte> Bytes;

  public bool IsFull => Length == Bytes.Length;

  long _lastAccessTicks;

  public int DeviceId;

  public long LastAccessTicks
  {
    get => Volatile.Read(ref _lastAccessTicks);
    set => Volatile.Write(ref _lastAccessTicks, value);
  }

  public object MaterializedEntries;

  public DecompressedBlock(
      int blockIndex,
      int blockSize,
      CompressionMethod compressionMethod,
      int compressionLevel)
  {
    BlockIndex = blockIndex;
    Length = 0;
    Bytes = new byte[blockSize];
    CompressionMethod = compressionMethod;
    CompressionLevel = compressionLevel;
  }

  public DecompressedBlock(
      int blockIndex,
      Memory<byte> bytes,
      CompressionMethod method,
      int compressionLevel)
  {
    BlockIndex = blockIndex;
    Length = bytes.Length;
    Bytes = bytes;
    CompressionMethod = method;
    CompressionLevel = compressionLevel;
  }

  internal int Capacity => Bytes.Length;

  /// <summary>
  /// Empties the block and gives it a new index, keeping its buffer.
  /// </summary>
  internal void Reset(int blockIndex)
  {
    BlockIndex = blockIndex;
    Length = 0;
  }

  public int Append(ReadOnlySpan<byte> data)
  {
    var remainingLength = Bytes.Length - Length;
    var copyLength = Math.Min(data.Length, remainingLength);
    data[..copyLength].CopyTo(Bytes.Span.Slice(Length));
    Length += copyLength;
    return copyLength;
  }

  public Memory<byte> Compress()
  {
    return DataCompression
        .Compress(
        CompressionMethod,
        CompressionLevel,
        Bytes.Slice(0, Length));
  }

  public static DecompressedBlock FromCompressed(
      int blockIndex, Memory<byte> compressedBytes,
      CompressionMethod method, int compressionLevel,
      int decompressedLength)
  {
    var decompressed = DataCompression
        .Decompress(method, compressedBytes, decompressedLength);
    return new DecompressedBlock(blockIndex, decompressed, method, compressionLevel);
  }

  public Memory<byte> GetBytes(int offset, int length)
  {
    if (offset + length > Length)
      length = Length - offset;
    return Bytes.Slice(offset, length);
  }

  public int TrimRight(long length)
  {
    var amount = Math.Min(length, Length);
    var newSize = Length - amount;
    Bytes = Bytes.Slice(0, (int)newSize).ToArray();
    Length = (int)newSize;
    return (int)amount;
  }
}
