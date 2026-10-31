using System.Runtime.InteropServices;
using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Compression;
using ZoneTree.Exceptions;
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

  // Guards the tail file. It is taken before SyncRoot,
  // so writes do not wait while the tail is synced to the disk.
  readonly Lock TailLock = new();

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

  // The main file of an existing stream can hold bytes that were not synced
  // to disk before this stream opened it, and truncations change it without
  // a sync. Appended data depends on those bytes, so a sync must include them.
  bool MainFileMayBeUnsynced;

  bool IsModified;

  // The number of bytes ever written to this stream.
  // The sync group counts the part of it that is synced to the disk.
  long WrittenByteCount;

  readonly DiskSyncGroup SyncGroup = new();

  // Once a write or a disk sync fails, the operating system may drop data
  // that was written before. Later syncs cannot prove durability anymore.
  volatile Exception DurabilityFailure;

  // The content position where the bytes that a power loss could tear start,
  // as found when this stream opened.
  readonly long UnsyncedTailStart;

  public string FilePath { get; }

  /// <summary>
  /// Syncs every committed block to the disk. A stream that is synced only
  /// once at the end, like a temporary replacement file, can disable it.
  /// </summary>
  internal bool SyncBlocksOnCommit { get; init; } = true;

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

  const int SectorSize = 512;

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
      MainFileMayBeUnsynced = true;
    }
    else
    {
      CompressionMethod = compressionMethod;
      WriteMetaData();
    }

    LoadTail();
    var mainFileLength = FileStream.Length;
    var lastBlockIndex = SkipToTheEnd(out var lastBlock);

    // The tail file names a later block once a committed block is synced. So a
    // block that the tail file does not name yet can be torn by a power loss
    // during its commit. Only unsynced records are lost by dropping it.
    if (lastBlockIndex >= TailBlock.BlockIndex && IsTornBlock(lastBlock))
    {
      FileStream.SetLength(lastBlock.Position);
      lastBlockIndex = SkipToTheEnd(out lastBlock);
    }
    FileStream.Position = FileStream.Length;

    // discard tail record if a greater block index exists in the main file.
    var isTailDiscarded = lastBlockIndex >= TailBlock.BlockIndex;
    if (isTailDiscarded)
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
    else if (TailBlock.Length == 0 && TailBlock.BlockIndex > lastBlockIndex + 1)
    {
      // Replacing the content restarts the block indexes and keeps the empty
      // tail. Skipping a torn block header needs the next block index in the tail.
      TailBlock = new DecompressedBlock(
          lastBlockIndex + 1, blockSize, CompressionMethod, CompressionLevel);
      LastWrittenTailIndex = -1;
      WriteTail();
    }
    // The tail file is the only other copy of a discarded tail, and writing the
    // new tail must not lose it while the block is not known to be on the disk.
    // Bytes that a truncation removed must not come back under new blocks.
    var isMainFileTruncated = FileStream.Length != mainFileLength;
    if (isMainFileTruncated)
      Logger.LogWarning($"Truncated the torn end of {FilePath} from {mainFileLength} to {FileStream.Length} bytes.");
    if (isTailDiscarded || isMainFileTruncated)
      SyncMainFile();
    // An empty tail file does not prove that the last block is synced: it can
    // be written while recovering a torn block that replaced a discarded tail.
    UnsyncedTailStart = _length - (TailBlock.Length > 0 ? TailBlock.Length : lastBlock.Length);
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
    lock (TailLock)
    {
      int blockIndex;
      int length;
      long writtenByteCount = -1;
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

        try
        {
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
        }
        catch (Exception e)
        {
          DurabilityFailure ??= e;
          throw;
        }
        blockIndex = tailBlock.BlockIndex;
        length = tailBlock.Length;
        // The bytes written so far are in the tail or in committed blocks,
        // and they are durable after this sync if the main file is synced.
        if (!MainFileMayBeUnsynced)
          writtenByteCount = WrittenByteCount;
      }
      // The tail bytes are copied into the tail file stream,
      // so writes can continue while they are synced to the disk.
      try
      {
        TailStream.Flush(true);
      }
      catch (Exception e)
      {
        DurabilityFailure ??= e;
        throw;
      }
      LastWrittenTailIndex = blockIndex;
      LastWrittenTailLength = length;
      if (writtenByteCount >= 0)
        SyncGroup.SyncedCount = writtenByteCount;
    }
  }

  /// <summary>
  /// Syncs everything written to this stream to the disk.
  /// A closed stream synced its content when it closed,
  /// so this only reports a failure of that sync.
  /// </summary>
  /// <exception cref="WriteAheadLogSyncFailedException">
  /// A write or a disk sync of this stream has failed.
  /// </exception>
  public void SyncToDisk()
  {
    if (!SyncGroup.Enter(Volatile.Read(ref WrittenByteCount)))
    {
      ThrowIfDurabilityFailed();
      return;
    }
    try
    {
      lock (TailLock)
      {
        lock (SyncRoot)
        {
          ThrowIfDurabilityFailed();
          if (IsClosed)
            return;
          SyncMainFileIfNeeded();
        }
        WriteTail();
      }
    }
    catch (Exception e) when (e is not WriteAheadLogSyncFailedException)
    {
      throw new WriteAheadLogSyncFailedException(FilePath, e);
    }
    finally
    {
      SyncGroup.Exit();
    }
  }

  void ThrowIfDurabilityFailed()
  {
    var failure = DurabilityFailure;
    if (failure != null)
      throw new WriteAheadLogSyncFailedException(FilePath, failure);
  }

  void SyncMainFileIfNeeded()
  {
    if (!MainFileMayBeUnsynced || !IsModified)
      return;
    SyncMainFile();
  }

  void SyncMainFile()
  {
    try
    {
      FileStream.Flush(true);
    }
    catch (Exception e)
    {
      DurabilityFailure ??= e;
      throw;
    }
    MainFileMayBeUnsynced = false;
  }

  /// <summary>
  /// Commits the tail block into the main file even when it is not full,
  /// and empties the tail file. The main file then holds the whole content
  /// on the disk, and the empty tail file is valid for any main file.
  /// </summary>
  internal void SealTail()
  {
    lock (TailLock)
      lock (SyncRoot)
      {
#pragma warning disable CA1513
        if (IsClosed)
          throw new global::System.ObjectDisposedException(GetType().FullName);
#pragma warning restore CA1513
        if (TailBlock.Length > 0)
        {
          CommitTailBlock();
          CurrentBlock = TailBlock;
          CurrentBlockPosition = 0;
        }
        if (MainFileMayBeUnsynced)
          SyncMainFile();
        WriteTail();
      }
  }

  /// <summary>
  /// Replaces the content of the compressed file that this stream opened,
  /// and closes this stream.
  /// </summary>
  /// <remarks>
  /// A crash at any step leaves either the old or the new content on disk:
  /// 1. The tail of this stream is sealed into its main file,
  ///    so the old content does not depend on the tail file anymore.
  /// 2. The new content is written into a sealed temporary file.
  /// 3. The temporary file replaces the main file atomically.
  /// The empty tail file is valid for both main files.
  /// </remarks>
  /// <param name="fileStreamProvider">The file stream provider of this stream.</param>
  /// <param name="writeContent">Writes the new content.</param>
  /// <returns>The length of the new content.</returns>
  internal long ReplaceContent(
      IFileStreamProvider fileStreamProvider,
      Action<BinaryWriter> writeContent)
  {
    lock (TailLock)
      lock (SyncRoot)
      {
        SealTail();
        Close();
      }
    var tmpFilePath = FilePath + ".tmp";
    var tmpTailFilePath = tmpFilePath + ".tail";
    if (fileStreamProvider.FileExists(tmpFilePath))
      fileStreamProvider.DeleteFile(tmpFilePath);
    if (fileStreamProvider.FileExists(tmpTailFilePath))
      fileStreamProvider.DeleteFile(tmpTailFilePath);
    long length;
    using (var tmpFileStream = new CompressedFileStream(
        Logger,
        fileStreamProvider,
        tmpFilePath,
        BlockSize,
        false,
        0,
        CompressionMethod,
        CompressionLevel)
    {
      // Nothing depends on the temporary file before it replaces the main
      // file, so sealing it syncs all of its blocks at once.
      SyncBlocksOnCommit = false
    })
    {
      using (var binaryWriter = new BinaryWriter(tmpFileStream, Encoding.UTF8, true))
      {
        writeContent(binaryWriter);
      }
      tmpFileStream.SealTail();
      length = tmpFileStream.Length;
    }
    fileStreamProvider.DeleteFile(tmpTailFilePath);
    fileStreamProvider.Replace(tmpFilePath, FilePath, null);
    return length;
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

  /// <summary>
  /// Tells whether a power loss tore the last block of the main file. The block
  /// does not decompress, or an appended sector of it did not persist and reads
  /// as zeros. An uncompressed block with a real zero sector is dropped too,
  /// which only loses records that the tail file does not hold, so unsynced ones.
  /// </summary>
  bool IsTornBlock(in LastBlockInfo block)
  {
    FileStream.Position = block.Position + 3 * sizeof(int);
    var compressedBytes = new byte[block.CompressedLength];
    ReadMainFileExactly(compressedBytes);
    Memory<byte> bytes;
    try
    {
      bytes = DataCompression.Decompress(CompressionMethod, compressedBytes, block.Length);
    }
    catch (Exception)
    {
      // Each compression library reports corrupted input with its own exception.
      return true;
    }
    return bytes.Length != block.Length ||
        HasZeroSector(compressedBytes, block.Position + 3 * sizeof(int));
  }

  static bool HasZeroSector(ReadOnlySpan<byte> bytes, long position)
  {
    while (bytes.Length > 0)
    {
      var length = (int)Math.Min(SectorSize - position % SectorSize, bytes.Length);
      var isZero = true;
      foreach (var value in bytes.Slice(0, length))
        isZero &= value == 0;
      if (isZero)
        return true;
      bytes = bytes.Slice(length);
      position += length;
    }
    return false;
  }

  /// <summary>
  /// Tells whether a damaged record that was read up to the
  /// given position can be in the bytes that a power loss tore.
  /// </summary>
  internal bool IsUnsyncedTail(long readPosition)
  {
    return readPosition > UnsyncedTailStart || readPosition >= _length;
  }

  struct LastBlockInfo
  {
    public long Position;

    public int CompressedLength;

    public int Length;
  }

  int SkipToTheEnd(out LastBlockInfo lastBlock)
  {
    Position = 0;
    _length = 0;
    FileStream.Position = MetaDataSize;
    var len = FileStream.Length;
    long physicalPosition = MetaDataSize;
    var lastBlockIndex = -1;
    lastBlock = default;
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
      // Block indexes only grow, so a smaller one is a header with a sector that
      // did not persist, if this can be the block being committed: the tail file
      // names a later block once a block is synced. An empty block is torn too,
      // unless the tail names a later block: older versions could commit empty blocks.
      var isTornEmptyBlock = compressedBlockSize == 0 && blockSize == 0 && blockIndex >= TailBlock.BlockIndex;
      if (compressedBlockSize < 0 || blockSize < 0 || blockEndPosition > len ||
          (blockIndex <= lastBlockIndex && lastBlockIndex + 1 >= TailBlock.BlockIndex) ||
          isTornEmptyBlock)
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
      lastBlock = new LastBlockInfo
      {
        Position = blockStartPosition,
        CompressedLength = compressedBlockSize,
        Length = blockSize
      };
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

    // Sync with tail writer
    lock (TailLock)
      lock (SyncRoot)
      {
        MainFileMayBeUnsynced = true;
        IsModified = true;
        // The next sync must write the truncated tail.
        SyncGroup.SyncedCount = -1;
        if (value != 0)
        {
          TruncateFile(value);
          return;
        }
        FileStream.SetLength(MetaDataSize);
        FileStream.Position = MetaDataSize;
        TailBlock = new DecompressedBlock(TailBlock.BlockIndex, BlockSize, CompressionMethod, CompressionLevel);
        SkipToTheEnd(out _);
        CurrentBlockPosition = 0;
        LastWrittenTailIndex = -1;
        WriteTail();
      }
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
      TruncateMainFile(truncatedLength);
    // Both paths must leave the stream ready to append at the new end.
    FileStream.Position = FileStream.Length;
    CurrentBlock = TailBlock;
    CurrentBlockPosition = TailBlock.Length;
    Position = _length;
  }

  /// <summary>
  /// Drops the blocks after the given content length from the main file.
  /// </summary>
  /// <remarks>
  /// A block that the new end falls into becomes the new tail, because rewriting
  /// it in place could lose it. A power loss at any step leaves all the kept content:
  /// 1. The tail is synced. It names the first dropped block, so opening
  ///    the old main file discards it and truncates the content again.
  /// 2. The main file is truncated and synced, so the
  ///    dropped bytes cannot come back under new blocks.
  /// </remarks>
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
      var blockIndex =
          BinarySerializerHelper.FromByteArray<int>(bytes, 0);
      var compressedBlockSize =
          BinarySerializerHelper.FromByteArray<int>(bytes, sizeof(int));
      var blockSize =
          BinarySerializerHelper.FromByteArray<int>(bytes, 2 * sizeof(int));
      if (off + blockSize > truncatedLength)
      {
        var diff = truncatedLength - off;
        // When the new end is at a block boundary, the tail stays empty.
        if (diff != 0)
        {
          bytes = BinaryReader.ReadBytes(compressedBlockSize);
          var truncatedBytes = DecompressedBlock
              .FromCompressed(
                  blockIndex, bytes,
                  CompressionMethod,
                  CompressionLevel,
                  blockSize).GetBytes(0, (int)diff);
          TailBlock = new DecompressedBlock(blockIndex, truncatedBytes, CompressionMethod, CompressionLevel);
        }
        WriteTail();
        FileStream.SetLength(physicalPosition);
        SyncMainFile();
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
#pragma warning disable CA1513
      if (IsClosed)
        throw new global::System.ObjectDisposedException(GetType().FullName);
#pragma warning restore CA1513
      if (Position != Length)
        throw new Exception("Compressed File Stream can only write to the end of the file.");

      IsModified = true;
      Volatile.Write(ref WrittenByteCount, WrittenByteCount + buffer.Length);
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
    try
    {
      var bw = BinaryWriter;
      bw.Write(tailBlock.BlockIndex);
      bw.Write(compressedBytes.Length);
      bw.Write(tailBlock.Length);
      bw.Write(compressedBytes.Span);
      FileStream.Flush(SyncBlocksOnCommit);
    }
    catch (Exception e)
    {
      DurabilityFailure ??= e;
      throw;
    }
    MainFileMayBeUnsynced = !SyncBlocksOnCommit;
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
    lock (TailLock)
      lock (SyncRoot)
      {
        if (IsClosed)
          return;
        StopTailWriter();
        WriteTail();
        SyncMainFileIfNeeded();
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

  protected override void Dispose(bool disposing)
  {
    Dispose();
  }
}

#pragma warning restore CA1063
#pragma warning restore CA2213
#pragma warning restore CA2215
