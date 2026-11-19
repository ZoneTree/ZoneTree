using ZoneTree.Compression;
using ZoneTree.Options;
using ZoneTree.Segments.Block;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

#pragma warning disable CA1063
#pragma warning disable CA2213
#pragma warning disable CA2215

public sealed unsafe partial class CompressedFileStream
{
  /// <summary>
  /// Loads the last sync checkpoint of the tail. A tail without an allocation never
  /// completed a sync, because the first sync allocates the checkpoint region.
  /// </summary>
  bool LoadSyncCheckpoint()
  {
    var tailLength = TailStream.Length;
    var version = WalTailFormat.ReadVersion(TailStream);
    if (version != 0 && version != WalTailFormat.CurrentVersion)
      throw new NotSupportedException($"Unsupported WAL tail format version {version}.");
    if (tailLength < TailHeaderSize + WalSyncCheckpoint.RegionSize ||
        tailLength % WalSyncCheckpoint.SlotSize != 0)
    {
      if (version == 0)
        return false;
      throw new InvalidDataException("The versioned WAL tail allocation is invalid.");
    }

    var offset = tailLength - WalSyncCheckpoint.RegionSize;
    var checkpoint = WalSyncCheckpoint.Read(TailStream, offset, out var sequence);
    // A power loss can persist the allocation without its descriptor sector.
    // Retain checkpoint recovery for that state.
    if (version == 0 && !checkpoint.HasValue && !MatchesCheckpointAllocation(offset))
      return false;
    CheckpointOffset = offset;
    CheckpointSequence = sequence;
    if (checkpoint.HasValue)
    {
      SyncedMainLength = checkpoint.Value.MainLength;
      SyncedContentLength = checkpoint.Value.ContentLength;
      if (FileStream.Length < SyncedMainLength)
        throw new InvalidDataException("The WAL is shorter than its synced prefix.");
    }
    // Repair only the descriptor; existing flush paths make it durable.
    if (version == 0)
      WriteTailFormatDescriptor();
    return checkpoint.HasValue;
  }

  bool MatchesCheckpointAllocation(long offset)
  {
    if (offset != GetCheckpointRegionOffset())
      return false;
    TailStream.Position = sizeof(int);
    var declaredLength = BinaryTailReader.ReadInt32();
    return declaredLength >= 0 && declaredLength <= offset - TailHeaderSize;
  }

  void RecoverMainFileAndTail()
  {
    LoadTail();
    var mainFileLength = FileStream.Length;
    var lastBlockIndex = SkipToTheEnd();
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
    else if (TailBlock.BlockIndex > lastBlockIndex + 1)
    {
      // Replacing the content restarts the block indexes and keeps the empty
      // tail. Skipping a torn block header needs the next block index in the tail.
      _length -= TailBlock.Length;
      TailBlock = CreateEmptyTail(lastBlockIndex + 1);
      WriteTailHeader(TailBlock.BlockIndex, 0);
      SyncTailFile();
    }
    // The tail file is the only other copy of a discarded tail, and writing the
    // new tail must not lose it while the block is not known to be on the disk.
    // Bytes that a truncation removed must not come back under new blocks.
    var isMainFileTruncated = FileStream.Length != mainFileLength;
    RecoveryTruncated = isMainFileTruncated;
    if (isMainFileTruncated)
      Logger.LogWarning($"Truncated the torn end of {FilePath} from {mainFileLength} to {FileStream.Length} bytes.");
    if (isTailDiscarded || isMainFileTruncated)
      SyncMainFile();
    if (isTailDiscarded)
      WriteTailHeader(TailBlock.BlockIndex, 0);
    if (_length < SyncedContentLength)
      throw new InvalidDataException("The WAL lost records inside its synced prefix.");
  }

  void InitializeRecoveredState(bool hasCheckpoint)
  {
    // Loaded bytes can have survived only in the OS cache. An explicit sync
    // after opening must cover them even before the first new append.
    SyncGroup.SyncedCount = hasCheckpoint && TailBlock.Length == 0 &&
        _length == SyncedContentLength && FileStream.Length == SyncedMainLength ? 0 : -1;
    if (SyncGroup.SyncedCount == 0)
      MainFileMayBeUnsynced = false;
    CurrentBlockPosition = 0;
    CurrentBlock = TailBlock;
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
    var len = CheckpointOffset > 0 ? CheckpointOffset : TailStream.Length;
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
  /// Tells whether a damaged record that was read up to the
  /// given position can be in the bytes that a power loss tore.
  /// </summary>
  internal bool IsUnsyncedTail(long recordPosition, long readPosition)
  {
    return recordPosition >= SyncedContentLength;
  }

  int SkipToTheEnd()
  {
    Position = 0;
    _length = 0;
    FileStream.Position = MetaDataSize;
    var length = FileStream.Length;
    long position = MetaDataSize;
    var lastIndex = -1;
    while (position < length)
    {
      try
      {
        var header = ReadMainBlockHeader();
        var end = position + WalBlockFrame.HeaderSize + (long)header.CompressedLength;
        // Blocks are never empty. An empty block is the zeros of a header whose
        // sectors did not persist.
        if (header.CompressedLength <= 0 || header.Length <= 0 || end > length ||
            (CompressionMethod == CompressionMethod.None && header.CompressedLength != header.Length) ||
            header.Index != lastIndex + 1)
          throw new InvalidDataException("Incomplete or invalid WAL block.");
        var payload = GetReadBuffer(ref CompressedReadBuffer, header.CompressedLength);
        ReadMainFileExactly(payload.Span);
        ValidateBlockPayload(payload.Span, header);
        lastIndex = header.Index;
        _length += header.Length;
        position = end;
        FileStream.Position = position;
      }
      catch (InvalidDataException) when (position >= SyncedMainLength)
      {
        // A checkpoint is a lower bound. Validate every frame, including when
        // later sectors persisted ahead of an earlier block during a power loss.
        FileStream.SetLength(position);
        FileStream.Position = position;
        break;
      }
    }
    _length += TailBlock.Length;
    CurrentBlock = TailBlock;
    CurrentBlockPosition = TailBlock.Length;
    Position = _length;
    return lastIndex;
  }
}

#pragma warning restore CA1063
#pragma warning restore CA2213
#pragma warning restore CA2215
