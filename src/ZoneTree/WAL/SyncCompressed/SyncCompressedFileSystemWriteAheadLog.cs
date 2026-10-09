using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions.WAL;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

#pragma warning disable CA2213

// https://devblogs.microsoft.com/dotnet/file-io-improvements-in-dotnet-6/
public sealed class SyncCompressedFileSystemWriteAheadLog<TKey, TValue> : IWriteAheadLog<TKey, TValue>
{
  readonly Lock SyncRoot = new();

  readonly ILogger Logger;

  readonly IFileStreamProvider FileStreamProvider;

  CompressedFileStream FileStream;

  BinaryWriter BinaryWriter;

  readonly ISerializer<TKey> KeySerializer;

  readonly ISerializer<TValue> ValueSerializer;

  public string FilePath { get; }

  public int CompressionBlockSize { get; }

  public CompressionMethod CompressionMethod { get; }

  public int CompressionLevel { get; }

  public bool EnableTailWriterJob { get; }

  public int TailWriterJobInterval { get; }

  public bool EnableIncrementalBackup { get; set; }

  public int InitialLength { get; private set; }

  public SyncCompressedFileSystemWriteAheadLog(
      ILogger logger,
      IFileStreamProvider fileStreamProvider,
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer,
      string filePath,
      WriteAheadLogOptions options)
  {
    Logger = logger;
    FileStreamProvider = fileStreamProvider;
    FilePath = filePath;
    CompressionBlockSize = options.CompressionBlockSize;
    CompressionMethod = options.CompressionMethod;
    CompressionLevel = options.CompressionLevel;
    EnableTailWriterJob = options.SyncCompressedModeOptions.EnableTailWriterJob;
    TailWriterJobInterval = options.SyncCompressedModeOptions.TailWriterJobInterval;
    KeySerializer = keySerializer;
    ValueSerializer = valueSerializer;
    CreateFileStream();
  }

  void CreateFileStream()
  {
    FileStream = new CompressedFileStream(
        Logger,
        FileStreamProvider,
        FilePath,
        CompressionBlockSize,
        EnableTailWriterJob,
        TailWriterJobInterval,
        CompressionMethod,
        CompressionLevel);
    BinaryWriter = new BinaryWriter(FileStream, Encoding.UTF8, true);
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
    FileStream.Dispose();
    if (FileStreamProvider.FileExists(FilePath))
      FileStreamProvider.DeleteFile(FilePath);
    var tailPath = FilePath + ".tail";
    if (FileStreamProvider.FileExists(tailPath))
      FileStreamProvider.DeleteFile(tailPath);
  }

  public WriteAheadLogReadLogEntriesResult<TKey, TValue> ReadLogEntries(
      bool stopReadOnException,
      bool stopReadOnChecksumFailure,
      bool sortByOpIndexes)
  {
    var result = WriteAheadLogEntryReader.ReadLogEntries<TKey, TValue, LogEntry>(
        Logger,
        FileStream,
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
    FileStream.Dispose();
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
                FileStream.GetFileContentIncludingTail);
      }

      var existingLength = FileStream.Length;
      long diff = 0;
      try
      {
        // Replacement crash recovery:
        // 1. Write keys and values to the tmp file.
        // 2. Use Replace API to replace target file.

        FileStream.Dispose();
        BinaryWriter = null;

        var tmpFilePath = FilePath + ".tmp";
        var tmpTailFilePath = FilePath + ".tmp.tail";
        var existingFileStream = FileStream;
        using (var tmpFileStream = new CompressedFileStream(
            Logger,
            FileStreamProvider,
            tmpFilePath,
            CompressionBlockSize,
            EnableTailWriterJob,
            TailWriterJobInterval,
            CompressionMethod,
            CompressionLevel))
        {
          FileStream = tmpFileStream;
          BinaryWriter = new BinaryWriter(tmpFileStream, Encoding.UTF8, true);
          tmpFileStream.SetLength(0);
          var len = keys.Length;
          for (var i = 0; i < len; ++i)
          {
            Append(keys[i], values[i], i);
          }
          diff = existingLength - FileStream.Length;
          FileStream = null;
        }

        // CompressedFileStream tail does not have 100% durability.
        // Therefore implementing replacement for tail with crash-resilience
        // is not necessary.
        FileStreamProvider.Replace(tmpFilePath, FilePath, null);
        FileStreamProvider.Replace(tmpTailFilePath, FilePath + ".tail", null);
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
        FileStream.Dispose();
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
