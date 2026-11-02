using System.Buffers.Binary;
using ZoneTree.AbstractFileStream;
using ZoneTree.Segments.Block;

namespace ZoneTree.WAL;

#pragma warning disable CA1063
#pragma warning disable CA2213
#pragma warning disable CA2215

public sealed unsafe partial class CompressedFileStream
{
  Task StartTailWriter()
  {
    // Mark the writer running before it starts.
    // Setting it on the writer thread would undo a Close() that happens
    // before the thread starts, leaving the writer loop running forever.
    IsTailWriterRunning = true;
    return Task.Factory.StartNew(
        TailWriteLoop,
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);
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
    PrepareCheckpointRegion();
    var localFileStream = (LocalFileStream)TailStream;
    var capacity = (int)Math.Min(BlockSize, CheckpointOffset - TailHeaderSize);
    MappedTail = new MappedTailFile(localFileStream.BaseFileStream, capacity);
    TailBlock = new DecompressedBlock(
        tailBlock.BlockIndex, MappedTail.Memory, CompressionMethod, CompressionLevel)
    {
      Length = tailBlock.Length
    };
  }

  void OpenTailForAppends(DecompressedBlock tailBlock)
  {
    WriteTailHeader(tailBlock.BlockIndex, tailBlock.Length);
    PrepareCheckpointRegion();
    var preparedBlock = new DecompressedBlock(
        tailBlock.BlockIndex, (int)Math.Min(BlockSize, CheckpointOffset - TailHeaderSize), CompressionMethod, CompressionLevel);
    preparedBlock.Append(tailBlock.GetBytes(0, tailBlock.Length).Span);
    TailBlock = preparedBlock;
  }

  long GetCheckpointRegionOffset() =>
      ((long)TailHeaderSize + BlockSize + WalSyncCheckpoint.SlotSize - 1) /
      WalSyncCheckpoint.SlotSize * WalSyncCheckpoint.SlotSize;

  void PrepareCheckpointRegion()
  {
    if (CheckpointOffset > 0)
      return;
    UsesSyncCheckpoints = true;
    CheckpointOffset = GetCheckpointRegionOffset();
    var length = CheckpointOffset + WalSyncCheckpoint.RegionSize;
    var oldLength = TailStream.Length;
    if (oldLength < length)
      WriteToTailFile(oldLength, new byte[length - oldLength]);
    else if (oldLength > length)
      TailStream.SetLength(length);
    // A smaller allocation can place its slots over old payload/padding.
    // Start both slots empty rather than reinterpreting those bytes as history.
    if (oldLength > CheckpointOffset)
      WriteToTailFile(CheckpointOffset, new byte[WalSyncCheckpoint.RegionSize]);
    WriteTailFormatDescriptor();
  }

  void WriteTailFormatDescriptor()
  {
    Span<byte> descriptor = stackalloc byte[WalTailFormat.DescriptorSize];
    WalTailFormat.Write(descriptor);
    WriteToTailFile(CheckpointOffset + WalSyncCheckpoint.RegionSize - descriptor.Length, descriptor);
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

  /// <summary>
  /// Empties the tail after its block is in the main file. A crash before
  /// the tail file changes is safe, because the main file holds the same block.
  /// </summary>
  void AdvanceTail(DecompressedBlock tailBlock)
  {
    if (TailHasDurableData)
    {
      // Only the first recovered tail needs this barrier. Ordinary appends
      // never put acknowledged data back into the reusable tail.
      if (MainFileMayBeUnsynced)
        SyncMainFile();
      TailHasDurableData = false;
    }
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
  }

}

#pragma warning restore CA1063
#pragma warning restore CA2213
#pragma warning restore CA2215
