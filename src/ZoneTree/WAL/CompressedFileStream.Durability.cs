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
    try
    {
      WalSyncCheckpoint.Write(TailStream, CheckpointOffset, ++CheckpointSequence, mainLength, contentLength);
    }
    catch (Exception e)
    {
      DurabilityFailure ??= e;
      throw;
    }
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
        bool writeCheckpoint;
        bool syncMain;
        lock (SyncRoot)
        {
          ThrowIfDurabilityFailed();
          if (IsClosed)
            return;
          Monitor.Enter(MainFileSyncRoot);
          try
          {
            writeCheckpoint = IsModified || IsTailPrepared || _length != 0 ||
                FileStream.Length != MetaDataSize || TailStream.Length != 0;
            var readPosition = Position;
            // Move the acknowledged bytes into the immutable main prefix.
            // The tail may be reused as soon as that copy reaches the OS.
            var sealedTail = TailBlock;
            FileStream.Position = FileStream.Length;
            if (sealedTail.Length > 0)
            {
              CommitTailBlock(false);
              AdvanceTail(sealedTail);
              CurrentBlock = TailBlock;
              CurrentBlockPosition = 0;
            }
            writtenByteCount = WrittenByteCount;
            mainLength = FileStream.Length;
            contentLength = _length;
            // Migrating a recovered durable tail can already have synced the
            // main prefix before reusing that tail. Do not sync it twice.
            syncMain = MainFileMayBeUnsynced;
            if (readPosition != _length)
              Seek(readPosition, SeekOrigin.Begin);
          }
          catch
          {
            Monitor.Exit(MainFileSyncRoot);
            throw;
          }
        }
        try
        {
          if (syncMain)
            SyncMainFile();
        }
        finally
        {
          // Release before taking SyncRoot: a writer can hold SyncRoot while
          // its rollover waits for the main-file sync to finish.
          Monitor.Exit(MainFileSyncRoot);
        }
        lock (SyncRoot)
        {
          ThrowIfDurabilityFailed();
          // Written after the main sync, this is only a recovery hint. If it
          // never persists, the block checksums still validate the main prefix.
          if (writeCheckpoint)
            WriteCheckpoint(mainLength, contentLength);
          SyncedMainLength = mainLength;
          SyncedContentLength = contentLength;
          SyncGroup.SyncedCount = writtenByteCount;
        }
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
    if (ClosedWithoutSync)
      throw new WriteAheadLogSyncFailedException(FilePath,
          new InvalidOperationException("This immutable WAL chunk must be reopened before syncing."));
  }

  void SyncMainFileIfNeeded()
  {
    if (!MainFileMayBeUnsynced || !IsModified)
      return;
    SyncMainFile();
  }

  void SyncMainFile()
  {
    lock (MainFileSyncRoot)
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

}

#pragma warning restore CA1063
#pragma warning restore CA2213
#pragma warning restore CA2215
