using System.Text;
using System.Runtime.CompilerServices;
using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions.WAL;
using ZoneTree.Logger;
using ZoneTree.Serializers;
using System;

namespace ZoneTree.WAL;

#pragma warning disable CA2213

// https://devblogs.microsoft.com/dotnet/file-io-improvements-in-dotnet-6/
public sealed class SyncFileSystemWriteAheadLog<TKey, TValue> : IWriteAheadLog<TKey, TValue>
{
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

  public bool EnableIncrementalBackup { get; set; }

  public int InitialLength { get; private set; }

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
      LogEntry.AppendLogEntry(BinaryWriter, keyBytes, valueBytes, opIndex);
    }
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
        sortByOpIndexes);
    InitialLength = result.Keys.Count;
    return result;
  }

  (bool isValid, TKey key, TValue value, long opIndex) DeserializeLogEntry(in LogEntry logEntry)
  {
    var isValid = logEntry.ValidateChecksum();
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
    if (!IsDisposed)
      FileStream.Flush(true);
  }

  public long ReplaceWriteAheadLog(TKey[] keys, TValue[] values, bool disableBackup)
  {
    lock (SyncRoot)
    {
      if (!disableBackup && EnableIncrementalBackup)
      {
        IncrementalLogAppender
            .AppendLogToTheBackupFile(
                FileStreamProvider,
                FilePath + ".full",
                () =>
                {
                  FileStream.Flush(true);
                  FileStream.Seek(0, SeekOrigin.Begin);
                  var existingLength = FileStream.Length;
                  var bytes = new byte[existingLength];
                  FileStream.ReadFaster(bytes, 0, bytes.Length);
                  return bytes;
                });
      }

      var existingLength = FileStream.Length;
      long diff = 0;
      try
      {
        // Replacement crash recovery:
        // 1. Write keys and values to the tmp file.
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
          LogEntry.AppendLogEntry(binaryWriter, keyBytes, valueBytes, i);
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
          memoryStream.CopyTo(tmpFileStream.ToStream());
          diff = existingLength - FileStream.Length;
          FileStream = null;
        }

        // atomic replacement using OS API.
        FileStreamProvider.Replace(tmpFilePath, FilePath, null);
        CreateFileStream();
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
    Task.Run(() =>
    {
      try
      {
        Dispose();
      }
      catch (Exception e)
      {
        Logger.LogError(e);
      }
    });
  }

  public void TruncateIncompleteTailRecord(IncompleteTailRecordFoundException incompleteTailException)
  {
    lock (SyncRoot)
    {
      FileStream.SetLength(incompleteTailException.RecordPosition);
    }
  }
}

#pragma warning restore CA2213
