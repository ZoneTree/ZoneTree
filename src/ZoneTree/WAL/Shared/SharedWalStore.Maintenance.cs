using System.Buffers.Binary;

namespace ZoneTree.WAL;

internal sealed partial class SharedWalStore<TKey, TValue>
{
  internal long Replace(long id, TKey[] keys, TValue[] values, long maximumOpIndex)
  {
    if (keys.Length != values.Length)
      throw new ArgumentException("Replacement keys and values must have the same length.");
    Task completion = null;
    long size = 0;
    lock (EnqueueLock)
    {
      ThrowIfUnavailable();
      var first = QueuedSequence + 1;
      QueuedSequence = checked(QueuedSequence + keys.LongLength + 2);
      Action action = () =>
      {
        lock (StorageLock)
        {
          var channel = GetChannel(id);
          channel.CacheValid = false;
          AppendRecord(new(SharedWalRecordKind.BeginReplacement, first, id, 0, default, default));
          for (var i = 0; i < keys.Length; ++i)
          {
            var key = KeySerializer.Serialize(keys[i]);
            var value = ValueSerializer.Serialize(values[i]);
            var record = new SharedWalRecord(SharedWalRecordKind.Data, first + 1 + i,
                id, Math.Max(0, maximumOpIndex - keys.LongLength + 1) + i, key, value);
            AppendRecord(record);
            size += record.Size;
          }
          var count = new byte[sizeof(long)];
          BinaryPrimitives.WriteInt64LittleEndian(count, keys.LongLength);
          var end = first + keys.LongLength + 1;
          AppendRecord(new(SharedWalRecordKind.EndReplacement, end, id, maximumOpIndex, default, count));
          channel.HasHistory = true;
          channel.ReplacementStart = first;
          channel.ReplacementEnd = end;
          channel.MaximumOpIndex = Math.Max(channel.MaximumOpIndex, maximumOpIndex);
          channel.ContentSize = size;
        }
        // Publication is an append-only end record. A crash before it leaves
        // the old view; after its sync the old ranges can be reclaimed.
        SyncCore(first + keys.LongLength + 1);
        Reclaim();
      };
      if (IsAsync)
      {
        completion = EnqueueControl(action);
      }
      else
      {
        try { action(); }
        catch (Exception e) { Fail(e); throw; }
      }
    }
    completion?.GetAwaiter().GetResult();
    return size;
  }

  internal void Retire(long id)
  {
    RunControl(() =>
    {
      // Drop is called after the owning disk segment and its metadata have
      // been published. It releases a logical view, never a shared file.
      long target;
      lock (StorageLock)
        target = Chunks.Select(chunk => chunk.References.GetValueOrDefault(id)).DefaultIfEmpty().Max();
      // The publication barrier normally covered this segment already. New
      // writes to other segments must not cause another fsync for each drop.
      SyncCore(target);
      lock (StorageLock)
        GetChannel(id).Retired = true;
      Reclaim();
    });
  }

  void Reclaim()
  {
    lock (SyncLock)
      lock (StorageLock)
      {
        var count = 0;
        while (count < Chunks.Count - 1)
        {
          var chunk = Chunks[count];
          var obsolete = chunk.References.All(pair =>
          {
            var channel = GetChannel(pair.Key);
            return channel.Retired ||
                (channel.ReplacementEnd <= SyncGroup.SyncedCount &&
                 channel.ReplacementStart > pair.Value);
          });
          if (!obsolete)
            break;
          ++count;
        }
        if (count == 0)
          return;
        var next = Chunks[count];
        var oldFirst = FirstChunkId;
        var oldBase = BaseSequence;
        FirstChunkId = next.Id;
        BaseSequence = next.BaseSequence;
        try { WriteManifest(); }
        catch
        {
          FirstChunkId = oldFirst;
          BaseSequence = oldBase;
          throw;
        }
        // The durable replay start moves before deletion. A crash during the
        // following deletes can leave harmless old files, never a missing prefix.
        var obsoleteChunks = Chunks.Take(count).ToArray();
        Chunks.RemoveRange(0, count);
        foreach (var chunk in obsoleteChunks)
        {
          chunk.Stream?.Dispose();
          chunk.Stream = null;
          DeleteChunk(chunk.Path);
        }
        var referenced = Chunks.SelectMany(chunk => chunk.References.Keys).ToHashSet();
        foreach (var id in Channels.Where(pair => pair.Value.Retired && !referenced.Contains(pair.Key))
                     .Select(pair => pair.Key).ToArray())
          Channels.Remove(id);
      }
  }
}
