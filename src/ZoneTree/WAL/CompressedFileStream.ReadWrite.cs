using System.IO.Hashing;
using System.Buffers.Binary;
using ZoneTree.Compression;
using ZoneTree.Options;
using ZoneTree.Segments.Block;

namespace ZoneTree.WAL;

#pragma warning disable CA1063
#pragma warning disable CA2213
#pragma warning disable CA2215

public sealed unsafe partial class CompressedFileStream
{
  bool LoadNextBlock()
  {
    if (FileStream.Position == FileStream.Length)
    {
      if (CurrentBlock.BlockIndex == TailBlock.BlockIndex)
        return false;
      CurrentBlockPosition = 0;
      CurrentBlock = TailBlock;
      return true;
    }
    CurrentBlockPosition = 0;
    var header = ReadMainBlockHeader();

    // Reuse the read buffers across blocks. Only CurrentBlock refers to a
    // block loaded for reading, and reads copy its bytes out before the next load.
    var compressedBytes = GetReadBuffer(ref CompressedReadBuffer, header.CompressedLength);
    ReadMainFileExactly(compressedBytes.Span);
    ValidateBlockPayload(compressedBytes.Span, header);
    var decompressedBytes = GetReadBuffer(ref DecompressedReadBuffer, header.Length);
    DataCompression.DecompressInto(CompressionMethod, compressedBytes, decompressedBytes);
    CurrentBlock = new DecompressedBlock(
        header.Index, decompressedBytes, CompressionMethod, CompressionLevel);
    return true;
  }

  readonly record struct MainBlockHeader(int Index, int CompressedLength, int Length, ulong Checksum, bool IsFramed)
  {
    public int Size => IsFramed ? WalBlockFrame.HeaderSize : 3 * sizeof(int);
  }

  MainBlockHeader ReadMainBlockHeader()
  {
    Span<byte> bytes = stackalloc byte[WalBlockFrame.HeaderSize];
    if (ReadMainFileAtMost(bytes[..12]) != 12)
      throw new InvalidDataException("Incomplete WAL block header.");
    if (WalBlockFrame.HasMagic(bytes))
    {
      if (ReadMainFileAtMost(bytes[12..]) != bytes.Length - 12 ||
          !WalBlockFrame.TryRead(bytes, out var index, out var compressedLength, out var length, out var checksum))
        throw new InvalidDataException("The WAL block header is invalid.");
      return new(index, compressedLength, length, checksum, true);
    }
    // Released 2.0.8 and earlier files retain their original prefix. Migration
    // appends only framed blocks, so there is no legacy writer or sync path.
    if (!HasLegacyBlocks)
      throw new InvalidDataException("The WAL block header is invalid.");
    return new(BinaryPrimitives.ReadInt32LittleEndian(bytes),
        BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
        BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]), 0, false);
  }

  static void ValidateBlockPayload(ReadOnlySpan<byte> payload, in MainBlockHeader header)
  {
    if (header.IsFramed && XxHash3.HashToUInt64(payload) != header.Checksum)
      throw new InvalidDataException("The WAL block checksum is invalid.");
  }

  static Memory<byte> GetReadBuffer(ref byte[] buffer, int length)
  {
    if (buffer == null || buffer.Length < length)
      buffer = new byte[length];
    return buffer.AsMemory(0, length);
  }

  /// <summary>
  /// Reads from the main file until the buffer is full or the file ends.
  /// </summary>
  int ReadMainFileAtMost(Span<byte> buffer)
  {
    var stream = BinaryReader.BaseStream;
    var total = 0;
    while (total < buffer.Length)
    {
      var read = stream.Read(buffer.Slice(total));
      if (read == 0)
        break;
      total += read;
    }
    return total;
  }

  void ReadMainFileExactly(Span<byte> buffer)
  {
    if (ReadMainFileAtMost(buffer) != buffer.Length)
      throw new EndOfStreamException("Incomplete compressed block.");
  }

  int ReadInternal(Span<byte> buffer)
  {
    var bytes = CurrentBlock.GetBytes(CurrentBlockPosition, buffer.Length);
    CurrentBlockPosition += bytes.Length;
    Position += bytes.Length;
    bytes.Span.CopyTo(buffer);
    return bytes.Length;
  }

  public override int Read(byte[] buffer, int offset, int count)
  {
    return Read(buffer.AsSpan(offset, count));
  }

  // BinaryReader reads every field through this overload.
  // The base implementation would rent and copy a buffer per call.
  public override int Read(Span<byte> buffer)
  {
    var initialCount = buffer.Length;
    var len = 0;
    while (true)
    {
      var readLen = ReadInternal(buffer);
      len += readLen;
      if (len == initialCount)
        return len;
      if (!CurrentBlock.IsFull)
        return len;
      buffer = buffer.Slice(readLen);
      if (!LoadNextBlock())
        return len;
    }
  }

  public override int ReadByte()
  {
    Span<byte> buffer = stackalloc byte[1];
    return Read(buffer) == 1 ? buffer[0] : -1;
  }

  void Skip(long offset)
  {
    if (offset <= 0)
      return;
    var targetPosition = Position + offset;
    while (offset > 0)
    {
      var remainingInBlock = CurrentBlock.Length - CurrentBlockPosition;
      if (offset <= remainingInBlock)
      {
        CurrentBlockPosition += (int)offset;
        Position = targetPosition;
        return;
      }
      offset -= remainingInBlock;
      CurrentBlockPosition = CurrentBlock.Length;
      if (!CurrentBlock.IsFull || !LoadNextBlock())
      {
        Position = targetPosition;
        return;
      }
    }
    Position = targetPosition;
  }

  public override long Seek(long offset, SeekOrigin origin)
  {
    if (offset < 0)
      throw new NotSupportedException("Negative seek is not supported.");
    switch (origin)
    {
      case SeekOrigin.Begin:
        Position = 0;
        FileStream.Position = MetaDataSize;
        CurrentBlockPosition = 0;
        Position = 0;
        LoadNextBlock();
        Skip(offset);
        break;
      case SeekOrigin.Current:
        Skip(offset);
        break;
      case SeekOrigin.End:
        // The constructor validated the main file, and writes and truncations
        // keep _length current, so there is no need to scan the blocks again.
        FileStream.Position = FileStream.Length;
        CurrentBlock = TailBlock;
        CurrentBlockPosition = TailBlock.Length;
        Position = _length;
        break;
    }
    return Position;
  }

  public override void Write(byte[] buffer, int offset, int count)
  {
    Write(buffer.AsSpan(offset, count));
  }

  // A WAL record can span several BinaryWriter calls. Keep its complete
  // append under the same lock as checkpoint sealing, including between fields.
  internal Lock.Scope BeginRecordWrite() => SyncRoot.EnterScope();

  public override void WriteByte(byte value)
  {
    Span<byte> buffer = stackalloc byte[1] { value };
    Write(buffer);
  }

  // BinaryWriter writes every field through this overload.
  // The base implementation would rent and copy a buffer per call.
  public override void Write(ReadOnlySpan<byte> buffer)
  {
    lock (SyncRoot)
    {
      // A closed stream cannot commit a full tail block,
      // so the append loop below would never finish.
#pragma warning disable CA1513
      if (IsClosed)
        throw new global::System.ObjectDisposedException(GetType().FullName);
#pragma warning restore CA1513
      if (Position != Length)
        throw new Exception("Compressed File Stream can only write to the end of the file.");

      if (!IsTailPrepared)
        PrepareTail();

      IsModified = true;
      IsTailFileUnsynced = true;
      Volatile.Write(ref WrittenByteCount, WrittenByteCount + buffer.Length);

      while (true)
      {
        var tailBlock = TailBlock;
        var tailLength = tailBlock.Length;
        var writeLen = Math.Min(buffer.Length, tailBlock.Capacity - tailLength);
        var mappedTail = MappedTail;
        if (mappedTail != null)
        {
          buffer.Slice(0, writeLen).CopyTo(new Span<byte>(mappedTail.Data + tailLength, writeLen));
          tailBlock.Length = tailLength + writeLen;
          mappedTail.Publish(tailBlock.BlockIndex, tailLength + writeLen);
        }
        else
        {
          // A failed write can leave a part of its bytes.
          // The explicit position lets the next write overwrite them.
          WriteToTailFile(TailHeaderSize + (long)tailLength, buffer.Slice(0, writeLen));
          tailBlock.Append(buffer.Slice(0, writeLen));
          WriteTailHeader(tailBlock.BlockIndex, tailBlock.Length);
        }
        _length += writeLen;
        Position += writeLen;
        buffer = buffer.Slice(writeLen);
        CurrentBlockPosition = CurrentBlock.Length;
        if (tailBlock.IsFull)
        {
          CommitTailBlock();
          CurrentBlock = TailBlock;
          CurrentBlockPosition = 0;
        }
        if (buffer.Length == 0)
          return;
      }
    }
  }

  void CommitTailBlock(bool advance = true)
  {
    if (IsClosed || !FileStream.CanWrite)
      return;
    var tailBlock = TailBlock;
    var compressedBytes = DataCompression.CompressInto(
        CompressionMethod,
        CompressionLevel,
        tailBlock.GetBytes(0, tailBlock.Length),
        ref CompressBuffer);
    lock (MainFileSyncRoot)
    {
      try
      {
        var bw = BinaryWriter;
        Span<byte> header = stackalloc byte[WalBlockFrame.HeaderSize];
        WalBlockFrame.Write(header, tailBlock.BlockIndex, tailBlock.Length, compressedBytes.Span);
        bw.Write(header);
        bw.Write(compressedBytes.Span);
        FileStream.Flush(false); // Use SyncWALAsync for power-loss durability.
      }
      catch (Exception e)
      {
        DurabilityFailure ??= e;
        throw;
      }
      MainFileMayBeUnsynced = true;
    }
    if (advance)
      AdvanceTail(tailBlock);
  }

}

#pragma warning restore CA1063
#pragma warning restore CA2213
#pragma warning restore CA2215
