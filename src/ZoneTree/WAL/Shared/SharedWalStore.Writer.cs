using System.Collections.Concurrent;
using ZoneTree.Exceptions;

namespace ZoneTree.WAL;

internal sealed partial class SharedWalStore<TKey, TValue>
{
  // The writer assigns journal sequence numbers in queue order. Commands use
  // segment zero and a separate queue, keeping tasks/delegates off data items.
  readonly record struct WorkItem(long SegmentId, long OpIndex, TKey Key, TValue Value);
  sealed record ControlWork(Action Action, TaskCompletionSource Completion);

  readonly ConcurrentQueue<WorkItem> Queue = new();
  readonly ConcurrentQueue<object> Commands = new();
  readonly ManualResetEventSlim WakeWriter = new();
  volatile bool Running;
  Task Writer;

  internal void Append(long id, in TKey key, in TValue value, long opIndex)
  {
    lock (EnqueueLock)
    {
      ThrowIfUnavailable();
      var sequence = checked(QueuedSequence + 1);
      if (IsAsync)
      {
        Queue.Enqueue(new(id, opIndex, key, value));
        QueuedSequence = sequence;
        if ((sequence & 4095) == 0)
          WakeWriter.Set();
      }
      else
      {
        WriteData(id, sequence, key, value, opIndex);
        QueuedSequence = sequence;
      }
    }
  }

  void WriteData(long id, long sequence, in TKey key, in TValue value, long opIndex)
  {
    ThrowIfFailed();
    var keyBytes = KeySerializer.Serialize(key);
    var valueBytes = ValueSerializer.Serialize(value);
    lock (StorageLock)
    {
      var channel = GetChannel(id);
      if (channel.Retired)
        throw new InvalidOperationException("The shared WAL segment has been retired.");
      var record = new SharedWalRecord(SharedWalRecordKind.Data, sequence, id, opIndex, keyBytes, valueBytes);
      // Serialization and validation above have not changed the journal. Once
      // writing starts, any failure can leave a partial record and is permanent.
      try
      {
        AppendRecord(record);
        channel.ContentSize += record.Size;
        channel.HasHistory = true;
        channel.CacheValid = false;
        channel.MaximumOpIndex = Math.Max(channel.MaximumOpIndex, opIndex);
      }
      catch (Exception e) { Fail(e); throw; }
    }
  }

  internal Task RequestSync()
  {
    long target;
    lock (EnqueueLock)
    {
      if (IsClosing || IsClosed)
        return FinalSync.Task;
      if (IsAsync)
      {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Commands.Enqueue(completion);
        Queue.Enqueue(default);
        WakeWriter.Set();
        return completion.Task;
      }
      target = QueuedSequence;
    }
    try { SyncCore(target); return Task.CompletedTask; }
    catch (Exception e) { return Task.FromException(e); }
  }

  public void Sync() => RequestSync().GetAwaiter().GetResult();

  void SyncCore(long target)
  {
    ThrowIfFailed();
    if (!SyncGroup.Enter(target))
    {
      ThrowIfFailed();
      return;
    }
    try
    {
      lock (SyncLock)
      {
        Chunk[] chunks;
        long covered;
        lock (StorageLock)
        {
          ThrowIfFailed();
          chunks = Chunks.ToArray();
          covered = WrittenSequence;
          if (covered < target)
            throw new InvalidOperationException("A shared WAL sync overtook its queued writes.");
        }
        // Chunk rotation never waits here. All preceding chunks are made
        // durable before acknowledging a prefix in a later physical file.
        var syncNames = false;
        foreach (var chunk in chunks)
        {
          syncNames |= !chunk.NameSynced;
          lock (chunk.StreamLock)
          {
            if (chunk.DurableSeal)
              continue;
            if (chunk.Stream == null)
            {
              if (!chunk.Sealed)
                throw new InvalidOperationException("An active shared WAL chunk lost its stream.");
              // Rotation copied the complete chunk, including its seal, into
              // this main file. Flush it directly; reopening CompressedFileStream
              // would rescan the whole chunk just to perform this disk sync.
              using var file = Files.CreateFileStream(chunk.Path, FileMode.Open,
                  FileAccess.ReadWrite, FileShare.None, bufferSize: 0);
              file.Flush(true);
              chunk.DurableSeal = true;
              continue;
            }
            var wasSealed = chunk.Sealed;
            chunk.Stream.SyncToDisk();
            if (wasSealed)
            {
              chunk.Stream.CloseAfterSync();
              chunk.Stream = null;
              chunk.DurableSeal = true;
            }
          }
          // Check after releasing StreamLock: a rotation that could not acquire
          // it leaves closing to this sync. If the seal comes after this check,
          // rotation can acquire the now-free lock and close the stream itself.
          // A seal appended during fsync still needs a later main-file sync.
          if (chunk.Sealed)
            CloseInactiveChunk(chunk);
        }
        if (syncNames)
        {
          // Fsync each captured chunk's contents before publishing its name.
          // Rotation during this sync can create further chunks: leave those
          // unmarked so the next sync also persists their namespace changes.
          Files.SyncDirectory(DirectoryPath);
          foreach (var chunk in chunks)
            chunk.NameSynced = true;
        }
        ThrowIfFailed();
        SyncGroup.SyncedCount = covered;
      }
    }
    catch (Exception e)
    {
      Fail(e);
      throw e is WriteAheadLogSyncFailedException ? e : new WriteAheadLogSyncFailedException(DirectoryPath, e);
    }
    finally { SyncGroup.Exit(); }
  }

  void RunControl(Action action)
  {
    Task completion = null;
    lock (EnqueueLock)
    {
      ThrowIfUnavailable();
      if (IsAsync)
      {
        completion = EnqueueControl(action);
      }
      else
        action();
    }
    completion?.GetAwaiter().GetResult();
  }

  Task EnqueueControl(Action action)
  {
    var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Commands.Enqueue(new ControlWork(action, source));
    Queue.Enqueue(default);
    WakeWriter.Set();
    return source.Task;
  }

  void StartWriter()
  {
    if (!IsAsync && !Options.SyncCompressedModeOptions.EnableTailWriterJob)
      return;
    Running = true;
    Writer = Task.Factory.StartNew(WriteLoop, CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);
  }

  void StopWriter()
  {
    Running = false;
    WakeWriter.Set();
    Writer?.GetAwaiter().GetResult();
    Writer = null;
    if (IsAsync)
      DrainQueue(false);
  }

  void WriteLoop()
  {
    while (Running)
    {
      if (IsAsync)
        DrainQueue(true);
      else if (HasNewWrites && Volatile.Read(ref WrittenSequence) > SyncGroup.SyncedCount)
        try { SyncCore(Volatile.Read(ref WrittenSequence)); }
        catch { /* The failure is sticky and future explicit syncs report it. */ }
      if (!Running)
        break;
      WakeWriter.Reset();
      // Reset before checking the queue: a producer racing the wait either
      // leaves work visible here or leaves the wake signal set.
      if (IsAsync && !Queue.IsEmpty)
        continue;
      var interval = IsAsync ? Options.AsyncCompressedModeOptions.EmptyQueuePollInterval :
          Options.SyncCompressedModeOptions.TailWriterJobInterval;
      if (interval == 0) Thread.Yield();
      else WakeWriter.Wait(interval);
    }
  }

  void DrainQueue(bool background)
  {
    List<TaskCompletionSource> requests = null;
    var remaining = -1;
    while (remaining != 0 && Queue.TryDequeue(out var item))
    {
      if (remaining > 0) --remaining;
      if (item.SegmentId != 0)
      {
        if (Failure == null)
          try { WriteData(item.SegmentId, WrittenSequence + 1, item.Key, item.Value, item.OpIndex); }
          catch (Exception e) { Fail(e); }
        continue;
      }
      Commands.TryDequeue(out var command);
      switch (command)
      {
        case ControlWork control:
          try { ThrowIfFailed(); control.Action(); control.Completion.TrySetResult(); }
          catch (Exception e) { Fail(e); control.Completion.TrySetException(e); }
          break;
        case TaskCompletionSource sync:
          if (remaining < 0) remaining = Queue.Count;
          (requests ??= new()).Add(sync);
          break;
      }
    }
    if (requests != null || (background && HasNewWrites && WrittenSequence > SyncGroup.SyncedCount && Queue.IsEmpty))
    {
      Exception error = null;
      try { SyncCore(WrittenSequence); }
      catch (Exception e) { error = e; }
      if (requests != null)
        foreach (var request in requests)
          if (error == null) request.TrySetResult();
          else request.TrySetException(error);
    }
  }
}
