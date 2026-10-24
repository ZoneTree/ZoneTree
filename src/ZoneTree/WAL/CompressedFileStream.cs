using System.Runtime.InteropServices;
using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Compression;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments.Block;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

#pragma warning disable CA1063
#pragma warning disable CA2213
#pragma warning disable CA2215

public sealed class CompressedFileStream : Stream, IDisposable
{
  readonly Lock SyncRoot = new();

  readonly ILogger Logger;

  readonly int BlockSize;

  readonly CompressionMethod CompressionMethod;

  readonly int CompressionLevel;

  readonly IFileStream FileStream;

  readonly IFileStream TailStream;

  volatile DecompressedBlock TailBlock;

  volatile DecompressedBlock CurrentBlock;

  volatile int CurrentBlockPosition;

  byte[] CompressedReadBuffer;

  byte[] DecompressedReadBuffer;

  byte[] CompressBuffer;

  readonly BinaryReader BinaryReader;

  readonly BinaryWriter BinaryWriter;

  readonly BinaryReader BinaryTailReader;

  readonly BinaryWriter BinaryTailWriter;

  long _length;

  volatile bool IsTailWriterRunning;

  internal Task TailWriterTask { get; }

  readonly int TailWriterJobInterval;

  volatile int LastWrittenTailIndex = -1;

  volatile int LastWrittenTailLength;

  volatile bool IsClosed;

  public string FilePath { get; }

  public override bool CanRead => FileStream.CanRead;

  public override bool CanSeek => FileStream.CanRead;

  public override bool CanWrite => FileStream.CanWrite;

  public override long Length => _length;

  public override long Position { get; set; }

  public struct CompressedFileMeta : IEquatable<CompressedFileMeta>
  {
    public CompressionMethod CompressionMethod;

    public CompressedFileMeta(CompressionMethod compressionMethod) : this()
    {
      CompressionMethod = compressionMethod;
    }

    public override bool Equals(object obj)
    {
      return obj is CompressedFileMeta meta && Equals(meta);
    }

    public bool Equals(CompressedFileMeta other)
    {
      return CompressionMethod == other.CompressionMethod;
    }

    public override int GetHashCode()
    {
      return HashCode.Combine(CompressionMethod);
    }

    public static bool operator ==(CompressedFileMeta left, CompressedFileMeta right)
    {
      return left.Equals(right);
    }

    public static bool operator !=(CompressedFileMeta left, CompressedFileMeta right)
    {
      return !(left == right);
    }
  }

  const int MetaDataSize = sizeof(byte);

  public CompressedFileStream(
      ILogger logger,
      IFileStreamProvider fileStreamProvider,
      string filePath,
      int blockSize,
      bool enableTailWriterJob,
      int tailWriterJobInterval,
      CompressionMethod compressionMethod,
      int compressionLevel)
  {
    CompressionLevel = compressionLevel;
    Logger = logger;
    FilePath = filePath;
    BlockSize = blockSize;
    FileStream = fileStreamProvider.CreateFileStream(filePath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None, blockSize);

    TailStream = fileStreamProvider.CreateFileStream(filePath + ".tail",
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None, blockSize);

    BinaryReader = new BinaryReader(FileStream.ToStream(), Encoding.UTF8, true);
    BinaryWriter = new BinaryWriter(FileStream.ToStream(), Encoding.UTF8, true);

    BinaryTailReader = new BinaryReader(TailStream.ToStream(), Encoding.UTF8, true);
    BinaryTailWriter = new BinaryWriter(TailStream.ToStream(), Encoding.UTF8, true);

    if (FileStream.Length > 0)
    {
      CompressionMethod = ReadMetaData().CompressionMethod;
    }
    else
    {
      CompressionMethod = compressionMethod;
      WriteMetaData();
    }

    LoadTail();
    var lastBlockIndex = SkipToTheEnd();

    // discard tail record if a greater block index exists in the main file.
    if (lastBlockIndex >= TailBlock.BlockIndex)
    {
      // if WAL had incomplete block in the end,
      // it was truncated in the skip to the end method.
      // Hence it is safe to discard the tail block
      // if the block index is equal or smaller.
      _length -= TailBlock.Length;
      Position -= TailBlock.Length;
      TailBlock = new DecompressedBlock(
          lastBlockIndex + 1, blockSize, CompressionMethod, CompressionLevel);
    }
    CurrentBlockPosition = 0;
    CurrentBlock = TailBlock;
    TailWriterJobInterval = tailWriterJobInterval;
    if (enableTailWriterJob)
    {
      // Mark the writer running before it starts.
      // Setting it on the writer thread would undo a Close() that happens
      // before the thread starts, leaving the writer loop running forever.
      IsTailWriterRunning = true;
      TailWriterTask = Task.Factory.StartNew(
          TailWriteLoop,
          CancellationToken.None,
          TaskCreationOptions.LongRunning,
          TaskScheduler.Default);
    }
  }

  CompressedFileMeta ReadMetaData()
  {
    FileStream.Position = 0;
    return new CompressedFileMeta(
        (CompressionMethod)BinaryReader.ReadByte());
  }

  void WriteMetaData()
  {
    FileStream.Position = 0;
    BinaryWriter.Write((byte)CompressionMethod);
    BinaryWriter.Flush();
    return;
  }

  void TailWriteLoop()
  {
    while (IsTailWriterRunning)
    {
      try
      {
        WriteTail();
      }
      catch (Exception e)
      {
        Logger.LogError(e);
      }
      if (TailWriterJobInterval == 0)
        Thread.Yield();
      else
        Thread.Sleep(TailWriterJobInterval);
    }
  }

  void StopTailWriter()
  {
    IsTailWriterRunning = false;
  }

  void LoadTail()
  {
    // read tail with tail tolerance.
    TailStream.Position = 0;
    var br = BinaryTailReader;

    var bytes = br.ReadBytes(2 * sizeof(int));
    var bytesLen = bytes.Length;
    if (bytesLen < sizeof(int))
    {
      TailBlock = new DecompressedBlock(0, BlockSize, CompressionMethod, CompressionLevel);
      return;
    }
    var blockIndex =
        BinarySerializerHelper.FromByteArray<int>(bytes, 0);
    if (bytesLen < 2 * sizeof(int))
    {
      TailBlock = new DecompressedBlock(blockIndex, BlockSize, CompressionMethod, CompressionLevel);
      return;
    }
    var decompressedLength =
        BinarySerializerHelper.FromByteArray<int>(bytes, sizeof(int));
    if (decompressedLength < 0)
    {
      TailBlock = new DecompressedBlock(blockIndex, BlockSize, CompressionMethod, CompressionLevel);
      return;
    }
    var len = TailStream.Length;
    var isTruncated = len < 2 * sizeof(int) + decompressedLength;
    if (isTruncated)
    {
      decompressedLength = (int)(len - 2 * sizeof(int));
      if (decompressedLength <= 0)
      {
        TailBlock = new DecompressedBlock(blockIndex, BlockSize, CompressionMethod, CompressionLevel);
        return;
      }
    }
    bytes = br.ReadBytes(decompressedLength);
    TailBlock = new DecompressedBlock(blockIndex, bytes, CompressionMethod, CompressionLevel);
    if (!isTruncated && bytes.Length == decompressedLength)
    {
      // The tail file already holds this block.
      // Closing an unchanged stream must not rewrite and fsync it.
      LastWrittenTailIndex = blockIndex;
      LastWrittenTailLength = decompressedLength;
    }
  }

  public void WriteTail()
  {
    var tailBlock = TailBlock;
    if (tailBlock.BlockIndex < LastWrittenTailIndex)
      return;
    if (tailBlock.BlockIndex == LastWrittenTailIndex &&
        tailBlock.Length == LastWrittenTailLength)
      return;
    if (IsClosed || !TailStream.CanWrite)
      return;
    lock (SyncRoot)
    {
      tailBlock = TailBlock;
      if (tailBlock.BlockIndex < LastWrittenTailIndex)
        return;
      if (tailBlock.BlockIndex == LastWrittenTailIndex &&
          tailBlock.Length == LastWrittenTailLength)
        return;
      if (IsClosed || !TailStream.CanWrite)
        return;

      var writtenLength = LastWrittenTailLength;
      if (tailBlock.BlockIndex == LastWrittenTailIndex &&
          tailBlock.Length > writtenLength)
      {
        // Within a block the tail only grows: truncations reset
        // LastWrittenTailIndex. Append the new bytes and then update the
        // length, which leaves the file identical to a full rewrite.
        TailStream.Position = 2 * sizeof(int) + writtenLength;
        BinaryTailWriter.Write(
            tailBlock.GetBytes(writtenLength, tailBlock.Length - writtenLength).Span);
        TailStream.Position = sizeof(int);
        BinaryTailWriter.Write(tailBlock.Length);
      }
      else
      {
        TailStream.Position = 0;
        BinaryTailWriter.Write(tailBlock.BlockIndex);
        BinaryTailWriter.Write(tailBlock.Length);
        var bytes = tailBlock.GetBytes(0, tailBlock.Length);
        BinaryTailWriter.Write(bytes.Span);
      }
      TailStream.Flush(true);
      LastWrittenTailIndex = tailBlock.BlockIndex;
      LastWrittenTailLength = tailBlock.Length;
    }
  }

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
    Span<byte> header = stackalloc byte[3 * sizeof(int)];
    ReadMainFileExactly(header);
    var blockIndex = MemoryMarshal.Read<int>(header);
    var compressedBlockSize = MemoryMarshal.Read<int>(header.Slice(sizeof(int)));
    var blockSize = MemoryMarshal.Read<int>(header.Slice(2 * sizeof(int)));

    // Reuse the read buffers across blocks. Only CurrentBlock refers to a
    // block loaded for reading, and reads copy its bytes out before the next load.
    var compressedBytes = GetReadBuffer(ref CompressedReadBuffer, compressedBlockSize);
    ReadMainFileExactly(compressedBytes.Span);
    var decompressedBytes = GetReadBuffer(ref DecompressedReadBuffer, blockSize);
    DataCompression.DecompressInto(CompressionMethod, compressedBytes, decompressedBytes);
    CurrentBlock = new DecompressedBlock(
        blockIndex, decompressedBytes, CompressionMethod, CompressionLevel);
    return true;
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

  int SkipToTheEnd()
  {
    Position = 0;
    _length = 0;
    FileStream.Position = MetaDataSize;
    var len = FileStream.Length;
    long physicalPosition = MetaDataSize;
    var lastBlockIndex = -1;
    Span<byte> header = stackalloc byte[3 * sizeof(int)];
    while (true)
    {
      if (physicalPosition == len)
        break;

      var blockStartPosition = physicalPosition;

      if (ReadMainFileAtMost(header) != header.Length)
      {
        // truncates partially written compressed block.
        // because the last block content is also written in tail block.
        FileStream.Position = blockStartPosition;
        FileStream.SetLength(blockStartPosition);
        break;
      }
      var blockIndex = MemoryMarshal.Read<int>(header);
      var compressedBlockSize = MemoryMarshal.Read<int>(header.Slice(sizeof(int)));
      var blockSize = MemoryMarshal.Read<int>(header.Slice(2 * sizeof(int)));

      var blockEndPosition = physicalPosition + sizeof(int) * 3L + compressedBlockSize;
      if (compressedBlockSize < 0 || blockSize < 0 || blockEndPosition > len)
      {
        // truncates partially written compressed block.
        // because the last block content is also written in tail block.
        FileStream.Position = blockStartPosition;
        FileStream.SetLength(blockStartPosition);
        break;
      }
      physicalPosition = blockEndPosition;
      FileStream.Position = physicalPosition;
      _length += blockSize;
      lastBlockIndex = blockIndex;
    }
    _length += TailBlock.Length;
    CurrentBlock = TailBlock;
    CurrentBlockPosition = TailBlock.Length;
    Position = _length;
    return lastBlockIndex;
  }

  public override void Flush()
  {
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

  public override void SetLength(long value)
  {
    if (value < 0)
      throw new Exception("File truncatedLength cannot be negative number!");

    if (value > _length)
      throw new Exception("Compressed file cannot be expanded with empty bytes.");

    if (value != 0)
    {
      // Sync with tail writer
      lock (SyncRoot)
      {
        TruncateFile(value);
      }
      return;
    }
    FileStream.SetLength(MetaDataSize);
    FileStream.Position = MetaDataSize;
    TailBlock = new DecompressedBlock(TailBlock.BlockIndex, BlockSize, CompressionMethod, CompressionLevel);
    SkipToTheEnd();
    CurrentBlockPosition = 0;
    LastWrittenTailIndex = -1;
    WriteTail();
  }

  void TruncateFile(long truncatedLength)
  {
    var trimmed = TailBlock.TrimRight(_length - truncatedLength);
    // The tail on disk no longer matches the trimmed tail block.
    // Comparing only index and length cannot detect this,
    // so force the next WriteTail to rewrite it.
    LastWrittenTailIndex = -1;
    _length -= trimmed;
    if (_length > truncatedLength)
    {
      // The tail is empty. Persist it before truncating the main file.
      WriteTail();
      TruncateMainFile(truncatedLength);
    }
    // Both paths must leave the stream ready to append at the new end.
    FileStream.Position = FileStream.Length;
    CurrentBlock = TailBlock;
    CurrentBlockPosition = TailBlock.Length;
    Position = _length;
  }

  void TruncateMainFile(long truncatedLength)
  {
    FileStream.Position = MetaDataSize;
    var len = FileStream.Length;
    // long: the main file and its decompressed content can exceed 2 GB.
    long physicalPosition = MetaDataSize;
    long off = 0;
    while (true)
    {
      if (physicalPosition >= len)
        break;

      var bytes = BinaryReader.ReadBytes(3 * sizeof(int));
      if (bytes.Length != 3 * sizeof(int))
      {
        FileStream.Position = physicalPosition;
        FileStream.SetLength(physicalPosition);
        break;
      }
#if DEBUG
      var blockIndex =
          BinarySerializerHelper.FromByteArray<int>(bytes, 0);
#endif
      var compressedBlockSize =
          BinarySerializerHelper.FromByteArray<int>(bytes, sizeof(int));
      var blockSize =
          BinarySerializerHelper.FromByteArray<int>(bytes, 2 * sizeof(int));
      if (off + blockSize > truncatedLength)
      {
        var diff = truncatedLength - off;
        if (diff == 0)
        {
          // The new end is at a block boundary: drop this and the following blocks.
          FileStream.SetLength(physicalPosition);
          _length = truncatedLength;
          break;
        }
        bytes = BinaryReader.ReadBytes(compressedBlockSize);
        var truncatedBytes = DecompressedBlock
            .FromCompressed(
                0, bytes,
                CompressionMethod,
                CompressionLevel,
                blockSize).GetBytes(0, (int)diff);
        var compressedBytes = new DecompressedBlock(0, truncatedBytes, CompressionMethod, CompressionLevel).Compress();
        FileStream.Position = physicalPosition + sizeof(int);
        var bw = BinaryWriter;
        bw.Write(compressedBytes.Length);
        bw.Write(truncatedBytes.Length);
        bw.Write(compressedBytes.Span);
        FileStream.SetLength(FileStream.Position);
        _length = truncatedLength;
        break;
      }
      physicalPosition += sizeof(int) * 3;
      physicalPosition += compressedBlockSize;
      FileStream.Position = physicalPosition;
      off += blockSize;
    }
  }

  public override void Write(byte[] buffer, int offset, int count)
  {
    Write(buffer.AsSpan(offset, count));
  }

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
      if (IsClosed)
        throw new global::System.ObjectDisposedException(GetType().FullName);
      if (Position != Length)
        throw new Exception("Compressed File Stream can only write to the end of the file.");

      var tailBlock = TailBlock;
      if (tailBlock.IsFull && tailBlock.Length < BlockSize)
      {
        // A tail loaded from disk or trimmed by a truncation is sized to its
        // content, so it is full at once. Grow it on the first write instead
        // of committing it as a short or empty block. Growing here rather than
        // on load avoids a full block allocation for streams that are only read.
        var grownTailBlock = new DecompressedBlock(
            tailBlock.BlockIndex, BlockSize, CompressionMethod, CompressionLevel);
        grownTailBlock.Append(tailBlock.GetBytes(0, tailBlock.Length).Span);
        TailBlock = grownTailBlock;
        CurrentBlock = grownTailBlock;
        CurrentBlockPosition = grownTailBlock.Length;
      }

      while (true)
      {
        var writeLen = TailBlock.Append(buffer);
        _length += writeLen;
        Position += writeLen;
        buffer = buffer.Slice(writeLen);
        CurrentBlockPosition = CurrentBlock.Length;
        if (TailBlock.IsFull)
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

  void CommitTailBlock()
  {
    if (IsClosed || !FileStream.CanWrite)
      return;
    var tailBlock = TailBlock;
    var compressedBytes = DataCompression.CompressInto(
        CompressionMethod,
        CompressionLevel,
        tailBlock.GetBytes(0, tailBlock.Length),
        ref CompressBuffer);
    var bw = BinaryWriter;
    bw.Write(tailBlock.BlockIndex);
    bw.Write(compressedBytes.Length);
    bw.Write(tailBlock.Length);
    bw.Write(compressedBytes.Span);
    FileStream.Flush(true);
    // The block is compressed and written, so its buffer can hold the next
    // tail block. This avoids allocating a new block buffer per commit.
    if (tailBlock.Capacity == BlockSize)
      tailBlock.Reset(tailBlock.BlockIndex + 1);
    else
      TailBlock = new DecompressedBlock(tailBlock.BlockIndex + 1, BlockSize, CompressionMethod, CompressionLevel);
  }

  public new void Dispose()
  {
    Close();
  }

  public override void Close()
  {
    if (IsClosed)
      return;
    lock (SyncRoot)
    {
      if (IsClosed)
        return;
      StopTailWriter();
      WriteTail();
      /*
       * Do not flush any file stream
       * and do not dispose any binary writer.
       * Because disposing binary writer also fires a flush.
       * BinaryWriter and binaryReaders are created
       * with leaveOpen flag = true.
       */
      FileStream.Close();
      TailStream.Close();
      IsClosed = true;
    }
  }

  public byte[] GetFileContent()
  {
    var currentPosition = FileStream.Position;
    FileStream.Position = 0;
    var bytes = new byte[FileStream.Length];
    FileStream.ReadFaster(bytes, 0, bytes.Length);
    FileStream.Position = currentPosition;
    return bytes;
  }

  public byte[] GetFileContentIncludingTail()
  {
    lock (SyncRoot)
    {
      var tailBlock = TailBlock;
      var compressedBytes = tailBlock.Compress();
      var currentPosition = FileStream.Position;
      FileStream.Position = 0;
      var bytes = new byte[FileStream.Length + compressedBytes.Length + 3 * sizeof(int)];
      using var ms = new MemoryStream(bytes);
      using var bw = new BinaryWriter(ms);
      FileStream.CopyTo(ms);
      bw.Write(tailBlock.BlockIndex);
      bw.Write(compressedBytes.Length);
      bw.Write(tailBlock.Length);
      bw.Write(compressedBytes.Span);
      ms.Flush();
      FileStream.Position = currentPosition;
      return bytes;
    }
  }

  protected override void Dispose(bool disposing)
  {
    Dispose();
  }
}

#pragma warning restore CA1063
#pragma warning restore CA2213
#pragma warning restore CA2215
