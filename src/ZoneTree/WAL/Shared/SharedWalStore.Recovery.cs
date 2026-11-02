using System.Buffers.Binary;

namespace ZoneTree.WAL;

internal sealed partial class SharedWalStore<TKey, TValue>
{
  void Recover()
  {
    WrittenSequence = BaseSequence;
    var paths = Files.GetFiles(DirectoryPath)
        .Where(path => Path.GetExtension(path) == ".wal")
        .Select(path => (Path: path, Id: long.TryParse(Path.GetFileNameWithoutExtension(path), out var id) ? id : 0))
        .Where(entry => entry.Id >= FirstChunkId).OrderBy(entry => entry.Id).ToArray();
    var expectedId = FirstChunkId;
    var stopped = false;
    var deletedSuffix = false;
    foreach (var entry in paths)
    {
      if (stopped)
      {
        DeleteChunk(entry.Path);
        deletedSuffix = true;
        continue;
      }
      if (entry.Id != expectedId)
        throw new InvalidDataException($"Missing shared WAL chunk {expectedId}.");
      var chunk = new Chunk
      {
        Id = entry.Id, Path = entry.Path,
        BaseSequence = WrittenSequence, LastSequence = WrittenSequence,
        Stream = OpenChunk(entry.Path)
      };
      Chunks.Add(chunk);
      ReadChunk(chunk, true);
      stopped = !chunk.Sealed || chunk.Stream.RecoveryTruncated;
      if (!stopped)
        CloseRecoveredChunk(chunk);
      ++expectedId;
      Current = chunk;
    }
    // Future rotation can reuse these chunk names. Persist the deletion first
    // so an abandoned suffix cannot reappear after another power loss.
    if (deletedSuffix)
      Files.SyncDirectory(DirectoryPath);
    if (Current == null)
      Current = CreateChunk(FirstChunkId, WrittenSequence);
    Current.Stream?.Seek(0, SeekOrigin.End);
  }

  static void CloseRecoveredChunk(Chunk chunk)
  {
    // Bound open file handles during recovery. A validated sealed prefix can
    // be made durable before inspecting later chunks; it is never modified.
    chunk.Stream.SyncToDisk();
    chunk.Stream.CloseAfterSync();
    chunk.Stream = null;
    chunk.DurableSeal = true;
  }

  void ReadChunk(Chunk chunk, bool repair)
  {
    var stream = chunk.Stream;
    stream.Seek(0, SeekOrigin.Begin);
    var hasHeader = false;
    while (stream.Position < stream.Length)
    {
      var start = stream.Position;
      SharedWalRecord record;
      try
      {
        record = SharedWalRecord.Read(stream);
      }
      catch (InvalidDataException) when (repair && stream.IsUnsyncedTail(start, stream.Position))
      {
        // A damaged record ends the entire shared prefix, including otherwise
        // valid later chunks. A per-segment reader must never make this choice.
        Logger?.LogWarning($"Truncated the incomplete shared WAL suffix in {chunk.Path} at {start}.");
        stream.SetLength(start);
        chunk.Sealed = false;
        break;
      }
      // A complete, checksummed envelope with a broken chain is corruption,
      // rather than the partially persisted end of a write.
      if (!hasHeader)
      {
        if (record.Kind != SharedWalRecordKind.ChunkHeader || record.SegmentId != chunk.Id ||
            record.OpIndex != StoreId || record.Sequence != WrittenSequence ||
            record.Key.Length != 0 || record.Value.Length != 0)
          throw new InvalidDataException("Invalid shared WAL chunk chain.");
        hasHeader = true;
        continue;
      }
      if (chunk.Sealed)
        throw new InvalidDataException("Records follow a sealed shared WAL chunk.");
      if (record.Kind == SharedWalRecordKind.SealChunk)
      {
        if (record.Sequence != WrittenSequence || record.SegmentId != chunk.Id + 1 ||
            record.Key.Length != 0 || record.Value.Length != 0)
          throw new InvalidDataException("Invalid shared WAL chunk boundary.");
        chunk.Sealed = true;
        continue;
      }
      if (record.Kind == SharedWalRecordKind.ChunkHeader || record.Sequence != WrittenSequence + 1 || record.SegmentId <= 0)
        throw new InvalidDataException("A shared WAL record is missing or out of order.");
      ApplyRecovered(record);
      chunk.References[record.SegmentId] = record.Sequence;
      chunk.LastSequence = WrittenSequence = record.Sequence;
    }
    if (!hasHeader)
    {
      if (!repair)
        throw new InvalidDataException("The shared WAL chunk header is missing.");
      stream.SetLength(0);
      new SharedWalRecord(SharedWalRecordKind.ChunkHeader, WrittenSequence, chunk.Id, StoreId, default, default).Write(stream);
    }
    chunk.Length = stream.Length;
  }

  void ApplyRecovered(in SharedWalRecord record)
  {
    var channel = GetChannel(record.SegmentId);
    switch (record.Kind)
    {
      case SharedWalRecordKind.Data:
        (channel.Pending ?? channel.Records).Add(record);
        if (channel.Pending == null)
        {
          channel.HasHistory = true;
          channel.ContentSize += record.Size;
        }
        channel.MaximumOpIndex = Math.Max(channel.MaximumOpIndex, record.OpIndex);
        break;
      case SharedWalRecordKind.BeginReplacement:
        if (channel.Pending != null)
          throw new InvalidDataException("Nested shared WAL replacements.");
        channel.Pending = new();
        channel.PendingStart = record.Sequence;
        break;
      case SharedWalRecordKind.EndReplacement:
        if (channel.Pending == null || record.Value.Length != sizeof(long) ||
            BinaryPrimitives.ReadInt64LittleEndian(record.Value.Span) != channel.Pending.Count)
          throw new InvalidDataException("Incomplete shared WAL replacement.");
        channel.Records = channel.Pending;
        channel.ContentSize = channel.Records.Sum(item => item.Size);
        channel.Pending = null;
        channel.ReplacementStart = channel.PendingStart;
        channel.ReplacementEnd = record.Sequence;
        channel.PendingStart = 0;
        channel.MaximumOpIndex = Math.Max(channel.MaximumOpIndex, record.OpIndex);
        channel.HasHistory = true;
        break;
      case SharedWalRecordKind.AbortReplacement:
        channel.Pending = null;
        channel.PendingStart = 0;
        break;
      default:
        throw new InvalidDataException("Invalid shared WAL record type.");
    }
  }

  void RefreshRecoveryCache()
  {
    var retired = Channels.Where(pair => pair.Value.Retired).Select(pair => pair.Key).ToHashSet();
    Channels.Clear();
    var written = WrittenSequence;
    WrittenSequence = BaseSequence;
    foreach (var chunk in Chunks)
    {
      chunk.Stream ??= OpenChunk(chunk.Path);
      chunk.Sealed = false;
      chunk.References.Clear();
      ReadChunk(chunk, false);
      if (chunk != Current)
        CloseRecoveredChunk(chunk);
    }
    if (WrittenSequence != written)
      throw new InvalidDataException("The shared WAL changed while reading.");
    foreach (var id in retired)
      GetChannel(id).Retired = true;
    Current.Stream?.Seek(0, SeekOrigin.End);
  }

  internal List<SharedWalRecord> Read(long id, out long maximumOpIndex)
  {
    List<SharedWalRecord> records = null;
    long maximum = 0;
    RunControl(() =>
    {
      lock (SyncLock)
        lock (StorageLock)
        {
          if (!GetChannel(id).CacheValid)
            RefreshRecoveryCache();
          var channel = GetChannel(id);
          // Transfer replay buffers to the caller. Once deserialized, the
          // journal must not keep a second copy of the tree's recovered data.
          records = channel.Records;
          channel.Records = new();
          channel.CacheValid = false;
          maximum = channel.MaximumOpIndex;
        }
    });
    maximumOpIndex = maximum;
    return records;
  }

  public void CompleteRecovery(IReadOnlyCollection<long> liveSegments)
  {
    RunControl(() =>
    {
      var live = liveSegments.ToHashSet();
      lock (StorageLock)
      {
        foreach (var (id, channel) in Channels)
          channel.Retired = !live.Contains(id);
      }
      Reclaim();
    });
  }

  void DeleteChunk(string path)
  {
    if (Files.FileExists(path))
      Files.DeleteFile(path);
    if (Files.FileExists(path + ".tail"))
      Files.DeleteFile(path + ".tail");
  }
}
