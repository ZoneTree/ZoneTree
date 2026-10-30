using System.Collections.Concurrent;
using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions;
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

  CompressedFileStream FileStream;

  BinaryWriter BinaryWriter;

  readonly ISerializer<TKey> KeySerializer;

  readonly ISerializer<TValue> ValueSerializer;

  readonly int CompressionBlockSize;

  readonly CompressionMethod CompressionMethod;

  readonly int CompressionLevel;

  readonly int EmptyQueuePollInterval;

  readonly ConcurrentQueue<QueueItem> Queue = new();

  // A queue item with this operation index is a sync request instead of a
  // record. Its task is in SyncRequests, so records do not grow by a field.
  // Records have positive operation indexes.
  const long SyncRequestOpIndex = long.MinValue;

  // The tasks of the sync requests in the queue, in queue order.
  // Each completes when the records queued before its request are synced.
  readonly ConcurrentQueue<TaskCompletionSource> SyncRequests = new();

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

  // Wakes the writer from its empty queue poll to stop or to serve a sync request.
  readonly ManualResetEventSlim WakeWriterSignal = new();

  readonly Lock SyncRequestLock = new();

  // Set when the WAL closes. Later sync requests wait for its final sync.
  TaskCompletionSource ClosedSyncRequest;

  volatile bool IsDropped;

  // A record that could not be written is lost, so no later record is written
  // and sync requests queued after it cannot be completed successfully anymore.
  volatile Exception RecordWriteFailure;

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
    CompressionBlockSize = options.CompressionBlockSize;
    CompressionMethod = options.CompressionMethod;
    CompressionLevel = options.CompressionLevel;
    FileStreamProvider = fileStreamProvider;
    KeySerializer = keySerializer;
    ValueSerializer = valueSerializer;
    CreateFileStream();
    StartWriter();
  }

  void CreateFileStream()
  {
    FileStream = new CompressedFileStream(
        Logger,
        FileStreamProvider,
        FilePath,
        CompressionBlockSize,
        false,
        0,
        CompressionMethod,
        CompressionLevel);
    BinaryWriter = new BinaryWriter(FileStream, Encoding.UTF8, true);
    FileStream.Seek(0, SeekOrigin.End);
  }

  void StartWriter()
  {
    StopWriter(false);
    WakeWriterSignal.Reset();
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
    // Wake the writer from its empty queue poll so stopping does not wait for the poll interval.
    WakeWriterSignal.Set();
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
    var syncRequests = new List<TaskCompletionSource>();
    lock (SyncRoot)
    {
      while (Queue.TryDequeue(out var q))
      {
        if (q.OpIndex != SyncRequestOpIndex)
          WriteRecord(in q);
        else
          CollectSyncRequest(syncRequests);
      }
    }
    if (syncRequests.Count > 0)
      SyncAndComplete(syncRequests);
    else
      FileStream.WriteTail();
  }

  void DoWrite()
  {
    var syncRequests = new List<TaskCompletionSource>();
    while (isRunning)
    {
      // The sync requests queued until the first one is dequeued share its
      // disk sync. Writing only the items queued by then keeps a busy queue
      // from delaying the sync. -1 means no sync request is dequeued yet.
      var batchLength = -1;
      while (isRunning && batchLength != 0 && Queue.TryDequeue(out var q))
      {
        if (batchLength > 0)
          --batchLength;
        if (q.OpIndex != SyncRequestOpIndex)
        {
          WriteRecord(in q);
          continue;
        }
        if (batchLength < 0)
          batchLength = Queue.Count;
        CollectSyncRequest(syncRequests);
      }
      if (syncRequests.Count > 0)
      {
        SyncAndComplete(syncRequests);
        syncRequests.Clear();
        continue;
      }
      if (isRunning && Queue.IsEmpty)
      {
        try
        {
          FileStream.WriteTail();
        }
        catch (Exception e)
        {
          LogWriterError(e);
        }
        if (!isRunning)
          break;
        if (EmptyQueuePollInterval == 0)
        {
          Thread.Yield();
        }
        else
        {
          WakeWriterSignal.Wait(EmptyQueuePollInterval);
          // An item queued before the signal is dequeued in the next loop.
          WakeWriterSignal.Reset();
        }
      }
    }
  }

  void WriteRecord(in QueueItem q)
  {
    // The log keeps the records before a failed one. A later record, such as a
    // transaction's commit, must not survive a crash without it. A partly written
    // record is an incomplete tail, which loading truncates.
    if (RecordWriteFailure != null)
      return;
    try
    {
      var keyBytes = KeySerializer.Serialize(q.Key);
      var valueBytes = ValueSerializer.Serialize(q.Value);
      AppendLogEntry(keyBytes, valueBytes, q.OpIndex);
    }
    catch (Exception e)
    {
      RecordWriteFailure ??= e;
      LogWriterError(e);
    }
  }

  /// <summary>
  /// Logs an error of the writer without throwing.
  /// </summary>
  /// <remarks>
  /// A failing logger must not stop the writer or skip completing sync
  /// requests, or queued records and sync requests would wait forever.
  /// </remarks>
  void LogWriterError(Exception e)
  {
    try
    {
      Logger.LogError(e);
    }
    catch
    {
    }
  }

  public void Append(in TKey key, in TValue value, long opIndex)
  {
    Queue.Enqueue(new QueueItem(in key, in value, opIndex));
  }

  public Task SyncAsync()
  {
    var syncRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    lock (SyncRequestLock)
    {
      if (ClosedSyncRequest != null)
        return ClosedSyncRequest.Task;
      SyncRequests.Enqueue(syncRequest);
      Queue.Enqueue(new QueueItem(default, default, SyncRequestOpIndex));
    }
    WakeWriterSignal.Set();
    return syncRequest.Task;
  }

  /// <summary>
  /// Takes the task of a dequeued sync request.
  /// </summary>
  void CollectSyncRequest(List<TaskCompletionSource> syncRequests)
  {
    // SyncAsync queues the task before the request item.
    if (SyncRequests.TryDequeue(out var syncRequest))
      syncRequests.Add(syncRequest);
  }

  /// <summary>
  /// Syncs the written records to the disk and completes the sync requests.
  /// </summary>
  void SyncAndComplete(List<TaskCompletionSource> syncRequests)
  {
    var failure = TrySyncToDisk();
    foreach (var syncRequest in syncRequests)
      CompleteSyncRequest(syncRequest, failure);
    // Log after completing, so callers get the failure even if logging fails.
    if (failure != null)
      LogWriterError(failure);
  }

  Exception TrySyncToDisk()
  {
    try
    {
      var recordWriteFailure = RecordWriteFailure;
      if (recordWriteFailure != null)
        throw new WriteAheadLogSyncFailedException(FilePath, recordWriteFailure);
      // A closed stream only reports whether its final sync failed.
      FileStream.SyncToDisk();
      return null;
    }
    catch (Exception e)
    {
      return e;
    }
  }

  void CompleteSyncRequest(TaskCompletionSource syncRequest, Exception failure)
  {
    // A dropped WAL either belongs to a dropped tree or its records were
    // merged into a disk segment that was synced to the disk before the drop.
    if (failure == null || IsDropped)
      syncRequest.TrySetResult();
    else
      syncRequest.TrySetException(failure);
  }

  /// <summary>
  /// Makes later sync requests wait for the final sync of the WAL.
  /// </summary>
  /// <returns>The final sync request.</returns>
  TaskCompletionSource CloseSyncRequests()
  {
    lock (SyncRequestLock)
    {
      return ClosedSyncRequest ??=
          new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
  }

  void Close()
  {
    var closedSyncRequest = CloseSyncRequests();
    try
    {
      StopWriter(true);
      FileStream.Dispose();
    }
    catch (Exception e)
    {
      CompleteSyncRequest(closedSyncRequest, e);
      throw;
    }
    CompleteSyncRequest(closedSyncRequest, TrySyncToDisk());
  }

  public void Drop()
  {
    IsDropped = true;
    var closedSyncRequest = CloseSyncRequests();
    // A merge can drop this WAL while MarkFrozen is still draining the queue.
    // Cancel the appends first so the drain cannot write to the closed stream.
    CancelWriter();
    Queue.Clear();
    StopWriter(false);
    FileStream.Dispose();
    if (FileStreamProvider.FileExists(FilePath))
      FileStreamProvider.DeleteFile(FilePath);
    var tailPath = FilePath + ".tail";
    if (FileStreamProvider.FileExists(tailPath))
      FileStreamProvider.DeleteFile(tailPath);
    // Every sync request of a dropped WAL completes successfully,
    // including those whose request items the clear removed.
    while (SyncRequests.TryDequeue(out var syncRequest))
      CompleteSyncRequest(syncRequest, null);
    CompleteSyncRequest(closedSyncRequest, null);
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
    Close();
  }

  public long ReplaceWriteAheadLog(TKey[] keys, TValue[] values, bool disableBackup)
  {
    lock (SyncRoot)
    {
      // Write the queued records into the existing file first.
      // If the replacement fails, the existing file keeps all of them.
      StopWriter(true);
      try
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
      finally
      {
        StartWriter();
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
      LogEntry.AppendLogEntry(binaryWriter, keyBytes, valueBytes, 0, IsLegacyFormat);
    }
  }

  public void MarkFrozen()
  {
    try
    {
      Close();
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
