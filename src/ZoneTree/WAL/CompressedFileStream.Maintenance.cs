using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Segments.Block;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

#pragma warning disable CA1063
#pragma warning disable CA2213
#pragma warning disable CA2215

public sealed unsafe partial class CompressedFileStream
{
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
        var sealedTail = TailBlock;
        if (sealedTail.Length > 0)
        {
          CommitTailBlock(false);
          SyncMainFile();
          AdvanceTail(sealedTail);
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
        ClearCheckpoints();
        Close();
        // The sealed old main file is durable without a checkpoint, and its
        // tail is empty. Both it and the sealed replacement can be recovered
        // without a checkpoint if a crash happens between these operations.
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
        UseMemoryMappedTail))
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
        // Truncation intentionally changes the durable prefix. First make the
        // current legacy main/tail representation durable, then invalidate its
        // old checkpoint before changing that representation.
        if (SyncedMainLength > 0 && value < SyncedContentLength)
        {
          SyncMainFile();
          SyncTailFile();
          ClearCheckpoints();
          SyncedMainLength = 0;
          SyncedContentLength = 0;
        }
        MainFileMayBeUnsynced = true;
        IsModified = true;
        // The next sync must write the truncated tail.
        SyncGroup.SyncedCount = -1;
        DetachTail();
        if (value != 0)
        {
          TruncateFile(value);
          CheckpointTruncation();
          return;
        }
        FileStream.SetLength(MetaDataSize);
        FileStream.Position = MetaDataSize;
        TailBlock = CreateEmptyTail(0);
        WriteTailHeader(TailBlock.BlockIndex, 0);
        SkipToTheEnd(out _);
        CurrentBlockPosition = 0;
        SyncTailFile();
        CheckpointTruncation();
      }
  }

  // Truncation is an explicit operation/recovery repair, not an append. Seal
  // its kept tail so subsequent ordinary writes cannot destroy kept records.
  void CheckpointTruncation()
  {
    var sealedTail = TailBlock;
    if (sealedTail.Length > 0)
    {
      CommitTailBlock(false);
      SyncMainFile();
      AdvanceTail(sealedTail);
      CurrentBlock = TailBlock;
      CurrentBlockPosition = 0;
    }
    if (MainFileMayBeUnsynced)
      SyncMainFile();
    WriteCheckpoint(FileStream.Length, _length);
    FlushTailFile();
    SyncedMainLength = FileStream.Length;
    SyncedContentLength = _length;
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
        else
        {
          // The new tail follows the kept prefix, even when truncation removed
          // several blocks that had already been sealed by a background sync.
          TailBlock = CreateEmptyTail(blockIndex);
          WriteTailHeader(blockIndex, 0);
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

}

#pragma warning restore CA1063
#pragma warning restore CA2213
#pragma warning restore CA2215
