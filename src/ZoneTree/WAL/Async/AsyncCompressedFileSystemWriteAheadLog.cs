using System.Collections.Concurrent;
using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions.WAL;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

#pragma warning disable CA2213

public sealed class AsyncCompressedFileSystemWriteAheadLog<TKey, TValue> : IWriteAheadLog<TKey, TValue>
{
  readonly Lock SyncRoot = new();

  readonly ILogger Logger;

  readonly IFileStreamProvider FileStreamProvider;

  readonly CompressedFileStream FileStream;

  readonly BinaryWriter BinaryWriter;

  readonly ISerializer<TKey> KeySerializer;

  readonly ISerializer<TValue> ValueSerializer;

  readonly int EmptyQueuePollInterval;

  readonly ConcurrentQueue<QueueItem> Queue = new();

  public struct QueueItem : IEquatable<QueueItem>
  {
    public TKey Key;

    public TValue Value;

    public long OpIndex;

    public QueueItem(in TKey key, in TValue value, long opIndex)
    {
      Key = key;
      Value = value;
      OpIndex = opIndex;
    }

    public override bool Equals(object obj)
    {
      return obj is QueueItem item && Equals(item);
    }

    public bool Equals(QueueItem other)
    {
      return EqualityComparer<TKey>.Default.Equals(Key, other.Key) &&
             EqualityComparer<TValue>.Default.Equals(Value, other.Value) &&
             OpIndex == other.OpIndex;
    }

    public override int GetHashCode()
    {
      return HashCode.Combine(Key, Value, OpIndex);
    }

    public static bool operator ==(QueueItem left, QueueItem right)
    {
      return left.Equals(right);
    }

    public static bool operator !=(QueueItem left, QueueItem right)
    {
      return !(left == right);
    }
  }

  readonly Lock AppendLock = new();

  volatile bool isRunning;

  volatile bool isWriterCancelled;

  volatile Task WriteTask;

  public string FilePath { get; }

  readonly bool IsLegacyFormat;

  public bool EnableIncrementalBackup { get; set; }

  public int InitialLength { get; private set; }

  public AsyncCompressedFileSystemWriteAheadLog(
      ILogger logger,
      IFileStreamProvider fileStreamProvider,
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer,
      string filePath,
      WriteAheadLogOptions options)
  {
    Logger = logger;
    FilePath = filePath;
    IsLegacyFormat = Path.GetFileNameWithoutExtension(filePath).EndsWith(WriteAheadLogProvider.LegacyWalExtension, StringComparison.Ordinal);
    EmptyQueuePollInterval = options.AsyncCompressedModeOptions.EmptyQueuePollInterval;
    FileStream = new CompressedFileStream(
        Logger,
        fileStreamProvider,
        filePath,
        options.CompressionBlockSize,
        false,
        0,
        options.CompressionMethod,
        options.CompressionLevel);
    BinaryWriter = new BinaryWriter(FileStream, Encoding.UTF8, true);
    FileStream.Seek(0, SeekOrigin.End);
    FileStreamProvider = fileStreamProvider;
    KeySerializer = keySerializer;
    ValueSerializer = valueSerializer;
    StartWriter();
  }

  void StartWriter()
  {
    StopWriter(false);
    isRunning = true;
    WriteTask = Task.Factory.StartNew(
        DoWrite,
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);
  }

  void StopWriter(bool consumeAll)
  {
    isRunning = false;
    WriteTask?.Wait();
    WriteTask = null;
    if (consumeAll)
      ConsumeQueue();
  }

  void CancelWriter()
  {
    lock (AppendLock)
    {
      isWriterCancelled = true;
    }
  }

  void ConsumeQueue()
  {
    lock (SyncRoot)
    {
      while (Queue.TryDequeue(out var q))
      {
        var keyBytes = KeySerializer.Serialize(q.Key);
        var valueBytes = ValueSerializer.Serialize(q.Value);
        AppendLogEntry(keyBytes, valueBytes, q.OpIndex);
      }
    }
    FileStream.WriteTail();
  }

  void DoWrite()
  {
    while (isRunning)
    {
      while (isRunning && Queue.TryDequeue(out var q))
      {
        try
        {
          var keyBytes = KeySerializer.Serialize(q.Key);
          var valueBytes = ValueSerializer.Serialize(q.Value);
          AppendLogEntry(keyBytes, valueBytes, q.OpIndex);
        }
        catch (Exception e)
        {
          Logger.LogError(e);
        }
      }
      if (isRunning && Queue.IsEmpty)
      {
        try
        {
          FileStream.WriteTail();
        }
        catch (Exception e)
        {
          Logger.LogError(e);
        }
        if (!isRunning)
          break;
        if (EmptyQueuePollInterval == 0)
          Thread.Yield();
        else
          Thread.Sleep(EmptyQueuePollInterval);
      }
    }
  }

  public void Append(in TKey key, in TValue value, long opIndex)
  {
    Queue.Enqueue(new QueueItem(in key, in value, opIndex));
  }

  public void Drop()
  {
    Queue.Clear();
    StopWriter(false);
    FileStream.Dispose();
    if (FileStreamProvider.FileExists(FilePath))
      FileStreamProvider.DeleteFile(FilePath);
    var tailPath = FilePath + ".tail";
    if (FileStreamProvider.FileExists(tailPath))
      FileStreamProvider.DeleteFile(tailPath);
  }

  void AppendLogEntry(Memory<byte> keyBytes, Memory<byte> valueBytes, long opIndex)
  {
    lock (AppendLock)
    {
      if (isWriterCancelled)
        return;
      LogEntry.AppendLogEntry(BinaryWriter, keyBytes, valueBytes, opIndex, IsLegacyFormat);
    }
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
    var isValid = IsLegacyFormat || logEntry.ValidateChecksum();
    var key = KeySerializer.Deserialize(logEntry.Key);
    var value = ValueSerializer.Deserialize(logEntry.Value);
    return (isValid, key, value, logEntry.OpIndex);
  }

  public void Dispose()
  {
    StopWriter(true);
    FileStream.Dispose();
  }

  public long ReplaceWriteAheadLog(TKey[] keys, TValue[] values, bool disableBackup)
  {
    lock (SyncRoot)
    {
      if (!disableBackup && EnableIncrementalBackup)
      {
        StopWriter(true);
        IncrementalLogAppender
            .AppendLogToTheBackupFile(
                FileStreamProvider,
                FilePath + ".full",
                FileStream.GetFileContentIncludingTail);
      }
      else
      {
        Queue.Clear();
        CancelWriter();
      }
      // Replacement crash recovery is not required here,
      // because the async-compressed write ahead log is not durable.
      // implementing crash recovery here does not make it durable.
      var existingLength = FileStream.Length;
      FileStream.SetLength(0);
      if (isWriterCancelled)
        isWriterCancelled = false;
      else
        StartWriter();
      var len = keys.Length;
      for (var i = 0; i < len; ++i)
      {
        Queue.Enqueue(new QueueItem(in keys[i], in values[i], 0));
      }
      return 0;
    }
  }

  public void MarkFrozen()
  {
    Task.Run(() =>
    {
      try
      {
        StopWriter(true);
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
