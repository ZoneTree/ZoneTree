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

  readonly bool IsLegacyFormat;

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
    IsLegacyFormat = Path.GetFileNameWithoutExtension(filePath).EndsWith(WriteAheadLogProvider.LegacyWalExtension, StringComparison.Ordinal);
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
      LogEntry.AppendLogEntry(BinaryWriter, keyBytes, valueBytes, opIndex, IsLegacyFormat);
    }
  }

  public Task SyncAsync()
  {
    try
    {
      CompressedFileStream fileStream;
      lock (SyncRoot)
      {
        fileStream = FileStream;
      }
      // Appends continue during the disk sync,
      // and a concurrent caller's sync can cover the records of this call.
      fileStream.SyncToDisk();
      return Task.CompletedTask;
    }
    catch (Exception e)
    {
      return Task.FromException(e);
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
        sortByOpIndexes,
        IsLegacyFormat ? null : (_, readPosition) => FileStream.IsUnsyncedTail(readPosition));
    InitialLength = result.Keys.Count;
    return result;
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
      try
      {
        var length = FileStream.ReplaceContent(
            FileStreamProvider,
            binaryWriter => AppendLogEntries(binaryWriter, keys, values));
        CreateFileStream();
        return existingLength - length;
      }
      catch (Exception e)
      {
        Logger.LogError(e);
        FileStream.Dispose();
        CreateFileStream();
        return existingLength - FileStream.Length;
      }
    }
  }

  void AppendLogEntries(BinaryWriter binaryWriter, TKey[] keys, TValue[] values)
  {
    var len = keys.Length;
    for (var i = 0; i < len; ++i)
    {
      var keyBytes = KeySerializer.Serialize(keys[i]);
      var valueBytes = ValueSerializer.Serialize(values[i]);
      LogEntry.AppendLogEntry(binaryWriter, keyBytes, valueBytes, i, IsLegacyFormat);
    }
  }

  public void MarkFrozen()
  {
    try
    {
      FileStream.Dispose();
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
    }
  }
}

#pragma warning restore CA2213
