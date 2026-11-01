using System.Buffers.Binary;
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

/// <summary>
/// The file stream of the compressed write ahead logs. Full blocks are compressed
/// into the main file, and the current block is in a separate tail file.
/// Writes survive a crash of the process as soon as they return.
/// A power loss still needs <see cref="SyncToDisk"/>.
/// </summary>
/// <remarks>
/// The tail file holds a block index, a block length and the block bytes.
/// A local file maps the tail file into memory, and the mapping is the tail block.
/// A file from any other file stream provider cannot be mapped, so every write
/// is appended to the tail file through the provider's stream. The file length
/// then tells the block length, as the tail load understands.
/// The streams of those providers must support a disk sync during a write.
/// </remarks>
public sealed unsafe class CompressedFileStream : Stream, IDisposable
{
  readonly Lock SyncRoot = new();

  // Guards the disk sync of the tail file. It is taken before SyncRoot,
  // so writes to a mapped tail do not wait while the tail is synced to the disk.
  readonly Lock TailLock = new();

  readonly ILogger Logger;

  readonly int BlockSize;

  readonly CompressionMethod CompressionMethod;

  readonly int CompressionLevel;

  readonly IFileStream FileStream;

  readonly IFileStream TailStream;

  readonly bool UseMemoryMappedTail;

  volatile DecompressedBlock TailBlock;

  volatile DecompressedBlock CurrentBlock;

  volatile int CurrentBlockPosition;

  byte[] CompressedReadBuffer;

  byte[] DecompressedReadBuffer;

  byte[] CompressBuffer;

  readonly BinaryReader BinaryReader;

  readonly BinaryWriter BinaryWriter;

  readonly BinaryReader BinaryTailReader;

  long _length;

  volatile bool IsTailWriterRunning;

  internal Task TailWriterTask { get; }

  readonly int TailWriterJobInterval;

  volatile bool IsClosed;

  // A tail that is not prepared is sized to its content, and the tail file holds
  // the same bytes. Streams that are only read stay in this state: they neither
  // change the tail file nor allocate a block. The first write prepares the tail.
  bool IsTailPrepared;

  // Null unless a prepared tail block is in the mapped tail file.
  MappedTailFile MappedTail;

  volatile bool IsTailFileUnsynced;

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

  const int MetaDataSize = sizeof(byte);

  const int SectorSize = 512;

  const int TailHeaderSize = MappedTailFile.HeaderSize;

  // A tail header with this length leaves the block length to the file length.
  // It is larger than any tail file, and adding the header size does not overflow.
  const int TailLengthInFileLength = int.MaxValue - TailHeaderSize;

  /// <param name="logger">The logger.</param>
  /// <param name="fileStreamProvider">The file stream provider.</param>
  /// <param name="filePath">The path of the main file.</param>
  /// <param name="blockSize">The size of a decompressed block.</param>
  /// <param name="enableTailWriterJob">
  /// Syncs the tail file to the disk periodically, which limits what a power
  /// loss can lose. Process crashes do not depend on it.
  /// </param>
  /// <param name="tailWriterJobInterval">The delay of the job in milliseconds.</param>
  /// <param name="compressionMethod">The compression method of a new file.</param>
  /// <param name="compressionLevel">The compression level.</param>
  /// <param name="useMemoryMappedTail">
  /// Maps the tail file into memory if it is a local file.
  /// Disabling it costs a system call per write.
  /// </param>
  public CompressedFileStream(
      ILogger logger,
      IFileStreamProvider fileStreamProvider,
      string filePath,
      int blockSize,
      bool enableTailWriterJob,
      int tailWriterJobInterval,
      CompressionMethod compressionMethod,
      int compressionLevel,
      bool useMemoryMappedTail = true)
  {
    CompressionLevel = compressionLevel;
    Logger = logger;
    FilePath = filePath;
    BlockSize = blockSize;
    FileStream = fileStreamProvider.CreateFileStream(filePath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None, blockSize);

    // The tail file has no buffer: a write is in the operating system when it returns.
    TailStream = fileStreamProvider.CreateFileStream(filePath + ".tail",
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None, 0);
    UseMemoryMappedTail = useMemoryMappedTail && TailStream is LocalFileStream;

    BinaryReader = new BinaryReader(FileStream.ToStream(), Encoding.UTF8, true);
    BinaryWriter = new BinaryWriter(FileStream.ToStream(), Encoding.UTF8, true);

    BinaryTailReader = new BinaryReader(TailStream.ToStream(), Encoding.UTF8, true);

    if (FileStream.Length > 0)
    {
      CompressionMethod = ReadMetaData();
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
      TailBlock = CreateEmptyTail(lastBlockIndex + 1);
    }
    else if (TailBlock.Length == 0 && TailBlock.BlockIndex > lastBlockIndex + 1)
    {
      // Replacing the content restarts the block indexes and keeps the empty
      // tail. Skipping a torn block header needs the next block index in the tail.
      TailBlock = CreateEmptyTail(lastBlockIndex + 1);
      WriteTailHeader(TailBlock.BlockIndex, 0);
      SyncTailFile();
    }
    // The tail file is the only other copy of a discarded tail, and writing the
    // new tail must not lose it while the block is not known to be on the disk.
    // Bytes that a truncation removed must not come back under new blocks.
    var isMainFileTruncated = FileStream.Length != mainFileLength;
    if (isMainFileTruncated)
      Logger.LogWarning($"Truncated the torn end of {FilePath} from {mainFileLength} to {FileStream.Length} bytes.");
    if (isTailDiscarded || isMainFileTruncated)
      SyncMainFile();
    if (isTailDiscarded)
      WriteTailHeader(TailBlock.BlockIndex, 0);
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

  CompressionMethod ReadMetaData()
  {
    FileStream.Position = 0;
    return (CompressionMethod)BinaryReader.ReadByte();
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

  DecompressedBlock CreateEmptyTail(int blockIndex)
  {
    return new DecompressedBlock(blockIndex, Memory<byte>.Empty, CompressionMethod, CompressionLevel);
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
      TailBlock = CreateEmptyTail(0);
      return;
    }
    var blockIndex =
        BinarySerializerHelper.FromByteArray<int>(bytes, 0);
    if (bytesLen < 2 * sizeof(int))
    {
      TailBlock = CreateEmptyTail(blockIndex);
      return;
    }
    var decompressedLength =
        BinarySerializerHelper.FromByteArray<int>(bytes, sizeof(int));
    if (decompressedLength < 0)
    {
      TailBlock = CreateEmptyTail(blockIndex);
      return;
    }
    var len = TailStream.Length;
    var isTruncated = len < 2 * sizeof(int) + decompressedLength;
    if (isTruncated)
    {
      decompressedLength = (int)(len - 2 * sizeof(int));
      if (decompressedLength <= 0)
      {
        TailBlock = CreateEmptyTail(blockIndex);
        return;
      }
    }
    bytes = br.ReadBytes(decompressedLength);
    TailBlock = new DecompressedBlock(blockIndex, bytes, CompressionMethod, CompressionLevel);
  }

  /// <summary>
  /// Writes the tail header with a single write,
  /// which a crash of the process cannot tear.
  /// </summary>
  void WriteTailHeader(int blockIndex, int length)
  {
    Span<byte> header = stackalloc byte[TailHeaderSize];
    BinaryPrimitives.WriteInt32LittleEndian(header, blockIndex);
    BinaryPrimitives.WriteInt32LittleEndian(header.Slice(sizeof(int)), length);
    WriteToTailFile(0, header);
  }

  void WriteToTailFile(long position, ReadOnlySpan<byte> bytes)
  {
    IsTailFileUnsynced = true;
    try
    {
      TailStream.Position = position;
      TailStream.Write(bytes);
    }
    catch (Exception e)
    {
      DurabilityFailure ??= e;
      throw;
    }
  }

  /// <summary>
  /// Makes the tail ready for the writes.
  /// </summary>
  void PrepareTail()
  {
    // A loaded block can be larger than the block size of this stream.
    if (TailBlock.Length >= BlockSize)
    {
      CommitTailBlock();
      CurrentBlock = TailBlock;
      CurrentBlockPosition = 0;
    }
    var tailBlock = TailBlock;
    try
    {
      if (UseMemoryMappedTail)
        MapTail(tailBlock);
      else
        OpenTailForAppends(tailBlock);
    }
    catch (Exception e)
    {
      DurabilityFailure ??= e;
      throw;
    }
    var isCurrentBlock = CurrentBlock == tailBlock;
    if (isCurrentBlock)
      CurrentBlock = TailBlock;
    IsTailPrepared = true;
  }

  void MapTail(DecompressedBlock tailBlock)
  {
    // The header can leave the block length to the file length,
    // so it must tell the length itself before the file grows.
    WriteTailHeader(tailBlock.BlockIndex, tailBlock.Length);
    var fileLength = TailStream.Length;
    var mappedFileLength = (long)TailHeaderSize + BlockSize;
    if (fileLength > mappedFileLength)
    {
      TailStream.SetLength(mappedFileLength);
    }
    else if (fileLength < mappedFileLength)
    {
      // Writing allocates the disk space now. Otherwise a full disk
      // could be found on a store into the mapping, which kills the process.
      WriteToTailFile(fileLength, new byte[mappedFileLength - fileLength]);
    }
    var localFileStream = (LocalFileStream)TailStream;
    MappedTail = new MappedTailFile(localFileStream.BaseFileStream, BlockSize);
    TailBlock = new DecompressedBlock(
        tailBlock.BlockIndex, MappedTail.Memory, CompressionMethod, CompressionLevel)
    {
      Length = tailBlock.Length
    };
  }

  void OpenTailForAppends(DecompressedBlock tailBlock)
  {
    TailStream.SetLength(TailHeaderSize + (long)tailBlock.Length);
    WriteTailHeader(tailBlock.BlockIndex, TailLengthInFileLength);
    var preparedBlock = new DecompressedBlock(
        tailBlock.BlockIndex, BlockSize, CompressionMethod, CompressionLevel);
    preparedBlock.Append(tailBlock.GetBytes(0, tailBlock.Length).Span);
    TailBlock = preparedBlock;
  }

  /// <summary>
  /// Moves a prepared tail back to memory that is sized to its content.
  /// The mapping can be released, and the rare truncations have a single path.
  /// </summary>
  void DetachTail()
  {
    if (!IsTailPrepared)
      return;
    var tailBlock = TailBlock;
    var detachedBlock = new DecompressedBlock(
        tailBlock.BlockIndex,
        tailBlock.GetBytes(0, tailBlock.Length).ToArray(),
        CompressionMethod,
        CompressionLevel);
    var isCurrentBlock = CurrentBlock == tailBlock;
    TailBlock = detachedBlock;
    if (isCurrentBlock)
      CurrentBlock = detachedBlock;
    IsTailPrepared = false;
    if (MappedTail == null)
      return;
    try
    {
      // A later disk sync of the file does not know about the released mapping.
      MappedTail.Flush();
    }
    catch (Exception e)
    {
      DurabilityFailure ??= e;
      throw;
    }
    finally
    {
      ((IDisposable)MappedTail).Dispose();
      MappedTail = null;
    }
  }

  void SyncTailFile()
  {
    if (!IsTailFileUnsynced)
      return;
    IsTailFileUnsynced = false;
    FlushTailFile();
  }

  void FlushTailFile()
  {
    try
    {
      MappedTail?.Flush();
      TailStream.Flush(true);
    }
    catch (Exception e)
    {
      DurabilityFailure ??= e;
      throw;
    }
  }

  /// <summary>
  /// Syncs the tail file to the disk. Every write is in the tail file already,
  /// so this only limits what a power loss can lose.
  /// </summary>
  public void WriteTail()
  {
    if (IsClosed || !IsTailFileUnsynced)
      return;
    lock (TailLock)
      SyncTailFileAndCountWrites();
  }

  /// <summary>
  /// Syncs the tail file and counts the writes that became durable with it.
  /// The caller holds the tail lock.
  /// </summary>
  void SyncTailFileAndCountWrites()
  {
    long writtenByteCount = -1;
    bool isTailFileUnsynced;
    lock (SyncRoot)
    {
      if (IsClosed)
        return;
      // The bytes written so far are in the tail or in committed blocks,
      // and they are durable after this sync if the main file is synced.
      if (!MainFileMayBeUnsynced)
        writtenByteCount = WrittenByteCount;
      // Everything written so far is in the tail file,
      // and later writes set the flag again.
      isTailFileUnsynced = IsTailFileUnsynced;
      IsTailFileUnsynced = false;
    }
    // Writes continue while the tail file is synced to the disk, and they can share
    // the next sync. The mapping stays valid because releasing it needs the tail lock.
    if (isTailFileUnsynced)
      FlushTailFile();
    if (writtenByteCount >= 0)
      SyncGroup.SyncedCount = writtenByteCount;
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
        SyncTailFileAndCountWrites();
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
        SyncTailFile();
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
        CompressionLevel,
        UseMemoryMappedTail)
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
      // unless the tail names a later block: a main file can hold committed empty blocks.
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

    // Sync with disk syncs of the tail.
    lock (TailLock)
      lock (SyncRoot)
      {
        MainFileMayBeUnsynced = true;
        IsModified = true;
        // The next sync must write the truncated tail.
        SyncGroup.SyncedCount = -1;
        DetachTail();
        if (value != 0)
        {
          TruncateFile(value);
          return;
        }
        FileStream.SetLength(MetaDataSize);
        FileStream.Position = MetaDataSize;
        TailBlock = CreateEmptyTail(TailBlock.BlockIndex);
        WriteTailHeader(TailBlock.BlockIndex, 0);
        SkipToTheEnd(out _);
        CurrentBlockPosition = 0;
        SyncTailFile();
      }
  }

  void TruncateFile(long truncatedLength)
  {
    var tailBlock = TailBlock;
    var trimmed = tailBlock.TrimRight(_length - truncatedLength);
    // The tail file keeps the bytes of the shorter tail at the same position.
    WriteTailHeader(tailBlock.BlockIndex, tailBlock.Length);
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
          // The tail length is zero until the header below covers these bytes.
          WriteToTailFile(TailHeaderSize, truncatedBytes.Span);
          WriteTailHeader(blockIndex, (int)diff);
        }
        SyncTailFile();
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

      if (!IsTailPrepared)
        PrepareTail();

      IsModified = true;
      IsTailFileUnsynced = true;
      Volatile.Write(ref WrittenByteCount, WrittenByteCount + buffer.Length);

      while (true)
      {
        var tailBlock = TailBlock;
        var tailLength = tailBlock.Length;
        var writeLen = Math.Min(buffer.Length, BlockSize - tailLength);
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
    AdvanceTail(tailBlock);
  }

  /// <summary>
  /// Empties the tail after its block is in the main file. A crash before
  /// the tail file changes is safe, because the main file holds the same block.
  /// </summary>
  void AdvanceTail(DecompressedBlock tailBlock)
  {
    var nextBlockIndex = tailBlock.BlockIndex + 1;
    if (!IsTailPrepared)
    {
      TailBlock = CreateEmptyTail(nextBlockIndex);
      WriteTailHeader(nextBlockIndex, 0);
      return;
    }
    // The block is compressed and written, so its buffer can hold the next
    // tail block. This avoids allocating a new block buffer per commit.
    tailBlock.Reset(nextBlockIndex);
    var mappedTail = MappedTail;
    if (mappedTail != null)
    {
      IsTailFileUnsynced = true;
      mappedTail.Publish(nextBlockIndex, 0);
      return;
    }
    // Recovery can drop a block that the tail names, because the tail holds its
    // bytes. So the tail must name the next block before it loses those bytes.
    WriteTailHeader(nextBlockIndex, 0);
    try
    {
      TailStream.SetLength(TailHeaderSize);
    }
    catch (Exception e)
    {
      DurabilityFailure ??= e;
      throw;
    }
    WriteTailHeader(nextBlockIndex, TailLengthInFileLength);
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
        SyncTailFile();
        SyncMainFileIfNeeded();
        // Reads of a closed stream can still reach the tail block.
        DetachTail();
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
