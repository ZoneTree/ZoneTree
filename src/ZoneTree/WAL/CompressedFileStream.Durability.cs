using ZoneTree.Exceptions;

namespace ZoneTree.WAL;

#pragma warning disable CA1063
#pragma warning disable CA2213
#pragma warning disable CA2215

public sealed unsafe partial class CompressedFileStream
{
  void WriteCheckpoint(long mainLength, long contentLength)
  {
    if (CheckpointOffset == 0)
    {
      WriteTailHeader(TailBlock.BlockIndex, TailBlock.Length);
      PrepareCheckpointRegion();
    }
    WalSyncCheckpoint.Write(TailStream, CheckpointOffset, ++CheckpointSequence, mainLength, contentLength);
    IsTailFileUnsynced = true;
  }

  void ClearCheckpoints()
  {
    if (CheckpointOffset == 0)
      return;
    // Keep the format descriptor when invalidating both checkpoint slots.
    WriteToTailFile(CheckpointOffset, new byte[WalSyncCheckpoint.RegionSize - WalTailFormat.DescriptorSize]);
    FlushTailFile();
    CheckpointSequence = 0;
  }

  void SyncTailFile()
  {
    if (!IsTailFileUnsynced)
      return;
    if (UsesSyncCheckpoints && CheckpointOffset == 0)
    {
      WriteTailHeader(TailBlock.BlockIndex, TailBlock.Length);
      PrepareCheckpointRegion();
    }
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
  /// Syncs the complete stream for the background writer. Unchanged content
  /// shares the last completed sync and does not issue another disk sync.
  /// </summary>
  public void WriteTail()
  {
    // Recovered records must be validated by the WAL reader before a background
    // sync can checkpoint them. Appends and truncation mark the stream modified.
    if (IsClosed || !IsModified)
      return;
    SyncToDisk();
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
        long writtenByteCount;
        long mainLength;
        long contentLength;
        lock (SyncRoot)
        {
          ThrowIfDurabilityFailed();
          if (IsClosed)
            return;
          // An unused WAL has only main-file metadata to sync. Keep its tail
          // empty until the first append; truncated/recovered tails still use
          // the checkpoint path below.
          if (!IsModified && !IsTailPrepared && _length == 0 &&
              FileStream.Length == MetaDataSize && TailStream.Length == 0)
          {
            SyncMainFile();
            SyncGroup.SyncedCount = WrittenByteCount;
            return;
          }
          var readPosition = Position;
          // A sync must not leave acknowledged bytes in a tail that
          // ordinary appends will reuse. Seal it without a per-block disk sync.
          var sealedTail = TailBlock;
          var hasTail = sealedTail.Length > 0;
          FileStream.Position = FileStream.Length;
          if (hasTail)
            CommitTailBlock(false);
          SyncMainFile();
          if (hasTail)
          {
            AdvanceTail(sealedTail);
            CurrentBlock = TailBlock;
            CurrentBlockPosition = 0;
          }
          writtenByteCount = WrittenByteCount;
          mainLength = FileStream.Length;
          contentLength = _length;
          WriteCheckpoint(mainLength, contentLength);
          IsTailFileUnsynced = false;
          if (readPosition != _length)
            Seek(readPosition, SeekOrigin.Begin);
        }
        // The main prefix is immutable now. Appends can continue while its
        // checkpoint is synced in a reserved sector of the existing tail file.
        FlushTailFile();
        SyncedMainLength = mainLength;
        SyncedContentLength = contentLength;
        SyncGroup.SyncedCount = writtenByteCount;
      }
    }
    catch (Exception e) when (e is not WriteAheadLogSyncFailedException)
    {
      DurabilityFailure ??= e;
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

}

#pragma warning restore CA1063
#pragma warning restore CA2213
#pragma warning restore CA2215
