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
/// Blocks in the main file validate their header and stored payload separately.
/// A sync seals the tail and syncs the main file once. Tail checkpoints are
/// recovery hints published afterwards, without a second disk sync.
/// Released legacy tails are sealed and synced before their allocation is reused.
/// </remarks>
public sealed unsafe partial class CompressedFileStream : Stream, IDisposable
{
  readonly Lock SyncRoot = new();

  // Serializes durable-prefix publication, truncation, replacement and close.
  // It is taken before SyncRoot; ordinary tail appends do not acquire it.
  readonly Lock TailLock = new();

  // Tail appends can continue during a main-file sync. A rollover waits here
  // before touching the main stream, without making the sync wait for SyncRoot.
  readonly object MainFileSyncRoot = new();

  readonly ILogger Logger;

  long CheckpointOffset;

  long CheckpointSequence;

  long SyncedMainLength;

  long SyncedContentLength;

  // False only while loading the variable-size tail of a released legacy WAL.
  bool UsesSyncCheckpoints;

  readonly int BlockSize;

  readonly CompressionMethod CompressionMethod;

  readonly bool HasLegacyBlocks;

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

  // Only immutable shared chunks use this close path. Their owner must reopen
  // the main file for a later disk sync rather than syncing this closed object.
  bool ClosedWithoutSync;

  // A tail that is not prepared is sized to its content, and the tail file holds
  // the same bytes. Streams that are only read stay in this state: they neither
  // change the tail file nor allocate a block. The first write prepares the tail.
  bool IsTailPrepared;

  // A recovered tail can be the only durable copy of records saved on close
  // or by a released version. Preserve that copy before its first reuse.
  bool TailHasDurableData;

  // Null unless a prepared tail block is in the mapped tail file.
  MappedTailFile MappedTail;

  volatile bool IsTailFileUnsynced;

  // The main file of an existing stream can hold bytes that were not synced
  // to disk before this stream opened it, and truncations change it without
  // a sync. Appended data depends on those bytes, so a sync must include them.
  bool MainFileMayBeUnsynced;

  volatile bool IsModified;

  // The number of bytes ever written to this stream.
  // The sync group counts the part of it that is synced to the disk.
  long WrittenByteCount;

  readonly DiskSyncGroup SyncGroup = new();

  // Once a write or a disk sync fails, the operating system may drop data
  // that was written before. Later syncs cannot prove durability anymore.
  volatile Exception DurabilityFailure;

  // The possible torn suffix of a released legacy WAL, before tail migration.
  readonly long UnsyncedTailStart;

  public string FilePath { get; }

  // Shared journals must also discard later physical chunks when a damaged
  // block ended this file's recoverable prefix.
  internal bool RecoveryTruncated { get; private set; }

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
  /// Syncs the complete stream periodically when it has unsynced changes.
  /// Completed syncs protect its prefix across later block rollovers.
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

    try
    {
      if (FileStream.Length > 0 && !IsFirstSectorUnwritten())
      {
        CompressionMethod = ReadMetaData(out HasLegacyBlocks);
        MainFileMayBeUnsynced = true;
      }
      else
      {
        // Recovery drops what follows an unwritten first sector, or reports
        // it as corruption when a checkpoint tells that it was synced.
        CompressionMethod = compressionMethod;
        WriteMetaData();
      }
      var hasCheckpoint = LoadSyncCheckpoint();
      UnsyncedTailStart = RecoverMainFileAndTail();
      TailHasDurableData = TailBlock.Length > 0;
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

  /// <summary>
  /// Tells whether a power loss kept the size of a new main file without its first
  /// sector, which then reads as zeros. The first disk sync of a main file covers
  /// that sector, so no sync completed and the file starts over as a new one.
  /// </summary>
  /// <remarks>
  /// Zero is also the metadata of a released LZ4 file. Reading these zeros as its
  /// block headers would keep empty blocks that were never written. A released
  /// file with a block that holds records has a nonzero byte in its first sector,
  /// even after an empty block of an older version. A released file without such
  /// a block keeps no records in its main file, so it can start over too.
  /// </remarks>
  bool IsFirstSectorUnwritten()
  {
    Span<byte> sector = stackalloc byte[SectorSize];
    FileStream.Position = 0;
    var length = ReadMainFileAtMost(sector);
    for (var i = 0; i < length; ++i)
    {
      if (sector[i] != 0)
        return false;
    }
    return true;
  }

  CompressionMethod ReadMetaData(out bool hasLegacyBlocks)
  {
    FileStream.Position = 0;
    var metadata = BinaryReader.ReadByte();
    hasLegacyBlocks = (metadata & WalBlockFrame.MetadataFlag) == 0;
    var method = (CompressionMethod)(metadata & ~WalBlockFrame.MetadataFlag);
    if (method > CompressionMethod.None)
      throw new NotSupportedException($"Unsupported WAL compression metadata {metadata}.");
    return method;
  }

  void WriteMetaData()
  {
    FileStream.Position = 0;
    BinaryWriter.Write((byte)((byte)CompressionMethod | WalBlockFrame.MetadataFlag));
    BinaryWriter.Flush();
    MainFileMayBeUnsynced = true;
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
      => Close(true);

  // A shared journal already synced the immutable main prefix. Its checkpoint
  // hint needs no extra disk sync when an inactive physical chunk is closed.
  internal void CloseAfterSync() => Close(false);

  /// <summary>
  /// Copies the remaining tail into the main file's OS cache and releases all
  /// handles and mappings without a disk sync. The caller must never append
  /// again, and must sync the main file before acknowledging its contents.
  /// </summary>
  internal void CloseForReopen()
  {
    lock (TailLock)
      lock (SyncRoot)
      {
        if (IsClosed)
          return;
        ThrowIfDurabilityFailed();
        StopTailWriter();
        if (TailBlock.Length > 0)
        {
          FileStream.Position = FileStream.Length;
          // Keep the tail copy and its header intact. Releasing the mapping
          // must not reuse the only durable copy of a recovered tail.
          CommitTailBlock(false);
        }

        ClosedWithoutSync = true;
        IsClosed = true;
        var mapping = MappedTail;
        MappedTail = null;
        IsTailPrepared = false;
        TailBlock = CreateEmptyTail(TailBlock.BlockIndex);
        CurrentBlock = TailBlock;
        try
        {
          // Dirty mapped pages already belong to the OS. An explicit mapping
          // flush is unnecessary: the main file now contains every record too.
          mapping?.DisposeWithoutFlush();
        }
        finally
        {
          try { FileStream.Close(); }
          finally { TailStream.Close(); }
        }
      }
  }

  void Close(bool syncTail)
  {
    if (IsClosed)
      return;
    lock (TailLock)
      lock (SyncRoot)
      {
        if (IsClosed)
          return;
        if (!syncTail && (TailBlock.Length != 0 || MainFileMayBeUnsynced))
          throw new InvalidOperationException("The WAL must be sealed and synced before closing without a tail sync.");
        StopTailWriter();
        if (syncTail)
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
