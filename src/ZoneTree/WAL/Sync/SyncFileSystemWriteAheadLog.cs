using System.Text;
using System.Runtime.CompilerServices;
using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions;
using ZoneTree.Exceptions.WAL;
using ZoneTree.Logger;
using ZoneTree.Serializers;
using System;

namespace ZoneTree.WAL;

#pragma warning disable CA2213

// https://devblogs.microsoft.com/dotnet/file-io-improvements-in-dotnet-6/
public sealed class SyncFileSystemWriteAheadLog<TKey, TValue> : IWriteAheadLog<TKey, TValue>
{
  const int SectorSize = 512;

  const int ScannedTailLength = 32 * 1024 * 1024;

  readonly Lock SyncRoot = new();

  readonly ILogger Logger;

  volatile bool IsDisposed;

  readonly IFileStreamProvider FileStreamProvider;

  IFileStream FileStream;

  BinaryWriter BinaryWriter;

  readonly ISerializer<TKey> KeySerializer;

  readonly ISerializer<TValue> ValueSerializer;

  readonly int FileStreamBufferSize;

  public string FilePath { get; }

  readonly bool IsLegacyFormat;

  public int InitialLength { get; private set; }

  long AppendedRecordCount;

  // Counts the appended records that are synced to the disk.
  readonly DiskSyncGroup SyncGroup = new();

  // Once a write or a disk sync fails, the operating system may drop records
  // that were written before. Later syncs cannot prove durability anymore.
  volatile Exception DurabilityFailure;

  public SyncFileSystemWriteAheadLog(
      ILogger logger,
      IFileStreamProvider fileStreamProvider,
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer,
      string filePath,
      int fileStreamBufferSize = 4096)
  {
    Logger = logger;
    FilePath = filePath;
    IsLegacyFormat = Path.GetFileNameWithoutExtension(filePath).EndsWith(WriteAheadLogProvider.LegacyWalExtension, StringComparison.Ordinal);
    FileStreamBufferSize = fileStreamBufferSize;
    FileStreamProvider = fileStreamProvider;
    KeySerializer = keySerializer;
    ValueSerializer = valueSerializer;
    CreateFileStream();
  }

  void CreateFileStream()
  {
    FileStream = FileStreamProvider.CreateFileStream(FilePath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None, FileStreamBufferSize);
    BinaryWriter = new BinaryWriter(FileStream.ToStream(), Encoding.UTF8, true);
    FileStream.Seek(0, SeekOrigin.End);
  }

  public void Append(in TKey key, in TValue value, long opIndex)
  {
    var keyBytes = KeySerializer.Serialize(key);
    var valueBytes = ValueSerializer.Serialize(value);
    lock (SyncRoot)
    {
      try
      {
        LogEntry.AppendLogEntry(BinaryWriter, keyBytes, valueBytes, opIndex, IsLegacyFormat);
      }
      catch (Exception e)
      {
        DurabilityFailure ??= e;
        throw;
      }
      Volatile.Write(ref AppendedRecordCount, AppendedRecordCount + 1);
    }
  }

  public Task SyncAsync()
  {
    try
    {
      SyncToDisk();
      return Task.CompletedTask;
    }
    catch (Exception e)
    {
      return Task.FromException(e);
    }
  }

  void SyncToDisk()
  {
    if (!SyncGroup.Enter(Volatile.Read(ref AppendedRecordCount)))
    {
      ThrowIfDurabilityFailed();
      return;
    }
    try
    {
      lock (SyncRoot)
      {
        ThrowIfDurabilityFailed();
        // Dispose syncs the file and drop happens after a merge moves the
        // records into a disk segment.
        if (IsDisposed)
          return;
        var count = AppendedRecordCount;
        try
        {
          FileStream.Flush(true);
        }
        catch (Exception e)
        {
          DurabilityFailure ??= e;
          throw new WriteAheadLogSyncFailedException(FilePath, e);
        }
        SyncGroup.SyncedCount = count;
      }
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

  public void Drop()
  {
    lock (SyncRoot)
    {
      if (!IsDisposed)
      {
        FileStream.Dispose();
        IsDisposed = true;
      }
      FileStreamProvider.DeleteFile(FilePath);
    }
  }

  public WriteAheadLogReadLogEntriesResult<TKey, TValue> ReadLogEntries(
      bool stopReadOnException,
      bool stopReadOnChecksumFailure,
      bool sortByOpIndexes)
  {
    var result = WriteAheadLogEntryReader.ReadLogEntries<TKey, TValue, LogEntry>(
        Logger,
        FileStream.ToStream(),
        stopReadOnException,
        stopReadOnChecksumFailure,
        LogEntry.ReadLogEntryWithStreamLength,
        DeserializeLogEntry,
        sortByOpIndexes,
        IsLegacyFormat ? null : IsUnsyncedTail);
    InitialLength = result.Keys.Count;
    return result;
  }

  /// <summary>
  /// Searches the bytes after a damaged record for what tells a torn tail from
  /// a corruption. Appended sectors persist in any order after the last disk
  /// sync, and a sector that did not persist reads as zeros up to its end. So a
  /// zero run up to a sector end means an unsynced tail, whatever follows it.
  /// Without one, a valid record after the damaged one means a corruption.
  /// </summary>
  /// <remarks>
  /// Only the first <see cref="ScannedTailLength"/> bytes are searched. When more
  /// bytes follow, only a damaged record that reaches the end of the log is a torn
  /// tail: an append of a long record that was cut off leaves no sign but its length.
  /// </remarks>
  bool IsUnsyncedTail(long recordPosition, long readPosition)
  {
    var stream = FileStream.ToStream();
    var buffer = new byte[Math.Min(stream.Length - recordPosition, ScannedTailLength)];
    stream.Position = recordPosition;
    FileStream.ReadFaster(buffer, 0, buffer.Length);
    stream.Position = readPosition;
    var bytes = buffer.AsSpan();
    var zeroRunStart = 0;
    for (var i = 0; i <= bytes.Length - LogEntry.MinimumRecordLength; ++i)
    {
      if ((recordPosition + i) % SectorSize == 0 &&
          zeroRunStart < i &&
          zeroRunStart <= Math.Max(0, i - SectorSize))
        return true;
      if (bytes[i] != 0)
        zeroRunStart = i + 1;
      var recordLength = LogEntry.ReadRecordLength(bytes[i..]);
      if (recordLength >= 0 &&
          recordLength <= bytes.Length - i &&
          LogEntry.HasValidChecksum(bytes.Slice(i, (int)recordLength)))
        return false;
    }
    return stream.Length - recordPosition <= buffer.Length || readPosition == stream.Length;
  }

  (bool isValid, TKey key, TValue value, long opIndex) DeserializeLogEntry(in LogEntry logEntry)
  {
    var isValid = IsLegacyFormat || logEntry.ValidateChecksum();
    // The bytes of a record that fails its checksum cannot be deserialized.
    if (!isValid)
      return (false, default, default, logEntry.OpIndex);
    var key = KeySerializer.Deserialize(logEntry.Key);
    var value = ValueSerializer.Deserialize(logEntry.Value);
    return (isValid, key, value, logEntry.OpIndex);
  }

  public void Dispose()
  {
    if (IsDisposed)
      return;
    lock (SyncRoot)
    {
      if (IsDisposed)
        return;
      Flush();
      FileStream.Dispose();
      IsDisposed = true;
    }
  }

  void Flush()
  {
    if (IsDisposed)
      return;
    try
    {
      FileStream.Flush(true);
    }
    catch (Exception e)
    {
      DurabilityFailure ??= e;
      throw;
    }
  }

  public long ReplaceWriteAheadLog(TKey[] keys, TValue[] values)
  {
    lock (SyncRoot)
    {
      var existingLength = FileStream.Length;
      long diff = 0;
      try
      {
        // Replacement crash recovery:
        // 1. Write keys and values to the tmp file and sync it to the disk.
        // 2. Use Replace API to replace target file.

        var tmpFilePath = FilePath + ".tmp";
        var existingFileStream = FileStream;
        var capacity = keys.Length * (Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>());
        using var memoryStream = new MemoryStream(capacity);
#pragma warning disable CA2000
        var binaryWriter = new BinaryWriter(memoryStream, Encoding.UTF8, true);
#pragma warning restore CA2000
        var len = keys.Length;
        for (var i = 0; i < len; ++i)
        {
          var keyBytes = KeySerializer.Serialize(keys[i]);
          var valueBytes = ValueSerializer.Serialize(values[i]);
          LogEntry.AppendLogEntry(binaryWriter, keyBytes, valueBytes, i, IsLegacyFormat);
        }

        FileStream.Dispose();
        BinaryWriter = null;

        using (var tmpFileStream = FileStreamProvider.CreateFileStream(
            tmpFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            FileStreamBufferSize))
        {
          FileStream = tmpFileStream;
          tmpFileStream.SetLength(0);
          memoryStream.Position = 0;
          memoryStream.CopyTo(tmpFileStream.ToStream());
          tmpFileStream.Flush(true);
          diff = existingLength - FileStream.Length;
          FileStream = null;
        }

        // atomic replacement using OS API.
        FileStreamProvider.Replace(tmpFilePath, FilePath, null);
        CreateFileStream();
        // The replaced content holds the appended records and it is synced.
        SyncGroup.SyncedCount = AppendedRecordCount;
      }
      catch (Exception e)
      {
        Logger.LogError(e);
        FileStream?.Dispose();
        CreateFileStream();
        diff = existingLength - FileStream.Length;
      }
      finally
      {
        if (FileStream == null)
          CreateFileStream();
        else if (FileStream.FilePath != FilePath)
        {
          FileStream.Dispose();
          CreateFileStream();
        }
      }
      return diff;
    }
  }

  public void MarkFrozen()
  {
    try
    {
      Dispose();
    }
    catch (Exception e)
    {
      Logger.LogError(e);
    }
  }

  public void TruncateIncompleteTailRecord(IncompleteTailRecordFoundException incompleteTailException)
  {
    lock (SyncRoot)
    {
      FileStream.SetLength(incompleteTailException.RecordPosition);
      // Otherwise a later power loss could bring the damaged bytes back
      // after new records, where they could look like a corruption.
      FileStream.Flush(true);
    }
  }
}

#pragma warning restore CA2213
