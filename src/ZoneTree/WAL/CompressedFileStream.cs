using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments.Block;

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
/// is copied through the provider's stream and updates the tail length header.
/// The fixed tail allocation reserves two checkpoint sectors beyond its data.
/// A version descriptor occupies the last 16 bytes of the reserved space.
/// The streams of those providers must support a disk sync during a write.
/// </remarks>
public sealed unsafe partial class CompressedFileStream : Stream, IDisposable
{
  readonly Lock SyncRoot = new();

  // Guards the disk sync of the tail file. It is taken before SyncRoot,
  // so writes to a mapped tail do not wait while the tail is synced to the disk.
  readonly Lock TailLock = new();

  readonly ILogger Logger;

  long CheckpointOffset;

  long CheckpointSequence;

  long SyncedMainLength;

  long SyncedContentLength;

  bool UsesSyncCheckpoints;

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

  public override bool CanRead => FileStream.CanRead;

  public override bool CanSeek => FileStream.CanRead;

  public override bool CanWrite => FileStream.CanWrite;

  public override long Length => _length;

  public override long Position { get; set; }

  const int MetaDataSize = sizeof(byte);

  const int SectorSize = 512;

  const int TailHeaderSize = MappedTailFile.HeaderSize;

  /// <param name="logger">The logger.</param>
  /// <param name="fileStreamProvider">The file stream provider.</param>
  /// <param name="filePath">The path of the main file.</param>
  /// <param name="blockSize">The size of a decompressed block.</param>
  /// <param name="enableTailWriterJob">
  /// Flushes the active tail periodically. Explicit SyncToDisk calls protect
  /// the complete stream across later block rollovers.
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

    try
    {
      var hasCheckpoint = LoadSyncCheckpoint();
      UnsyncedTailStart = RecoverMainFileAndTail();
      InitializeRecoveredState(hasCheckpoint);
    }
    catch
    {
      // A rejected format must not leave files locked by a failed constructor.
      TailStream.Dispose();
      FileStream.Dispose();
      throw;
    }
    TailWriterJobInterval = tailWriterJobInterval;
    if (enableTailWriterJob)
      TailWriterTask = StartTailWriter();
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

  public override void Flush()
  {
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
