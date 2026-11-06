using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

internal interface ISharedWalStore
{
  bool IsClosed { get; }
  bool IsClosing { get; }
  void WaitForClose();
  void Sync();
  void CompleteRecovery(IReadOnlyCollection<long> liveSegments);
  void Close();
}

/// <summary>
/// One ordered journal for all segment handles in a tree. The enqueue lock
/// establishes causal order; the storage lock protects append and replay state;
/// the sync lock serializes disk syncs and physical chunk reclamation.
/// </summary>
internal sealed partial class SharedWalStore<TKey, TValue> : ISharedWalStore, IDisposable
{
  internal const string DirectoryName = "shared";
  internal const string ManifestName = "manifest";
  const ulong ManifestMagic = 0x314C41574853545A; // ZTSHWAL1
  const int ManifestSize = 48;

  readonly Lock EnqueueLock = new();
  readonly Lock StorageLock = new();
  readonly Lock SyncLock = new();
  readonly IFileStreamProvider Files;
  readonly ILogger Logger;
  readonly WriteAheadLogOptions Options;
  readonly ISerializer<TKey> KeySerializer;
  readonly ISerializer<TValue> ValueSerializer;
  readonly string ManifestPath;
  readonly List<Chunk> Chunks = new();
  readonly Dictionary<long, Channel> Channels = new();
  readonly DiskSyncGroup SyncGroup = new();
  readonly TaskCompletionSource FinalSync = new(TaskCreationOptions.RunContinuationsAsynchronously);

  long StoreId;
  long FirstChunkId = 1;
  long BaseSequence;
  long WrittenSequence;
  long QueuedSequence;
  int HandleCount;
  bool HasNewWrites;
  volatile Exception Failure;
  Chunk Current;

  internal string DirectoryPath { get; }
  internal WriteAheadLogMode Mode { get; private set; }
  internal bool IsAsync => Mode == WriteAheadLogMode.AsyncShared;
  public bool IsClosed { get; private set; }
  public bool IsClosing { get; private set; }

  sealed class Chunk
  {
    internal long Id;
    internal string Path;
    internal long BaseSequence;
    internal long LastSequence;
    internal long Length;
    internal volatile bool Sealed;
    internal bool DurableSeal;
    internal bool NameSynced;
    internal CompressedFileStream Stream;
    // Rotation holds StorageLock; syncs release it while flushing. This lock
    // protects handle lifetime without making those two locks depend on each other.
    internal readonly object StreamLock = new();
    internal readonly Dictionary<long, long> References = new();
  }

  sealed class Channel
  {
    internal bool Retired;
    internal bool CacheValid = true;
    internal bool HasHistory;
    internal long MaximumOpIndex;
    internal long ContentSize;
    internal long ReplacementStart;
    internal long ReplacementEnd;
    internal long PendingStart;
    internal List<SharedWalRecord> Records = new();
    internal List<SharedWalRecord> Pending;
  }

  internal SharedWalStore(ILogger logger, IFileStreamProvider files, string directory,
      WriteAheadLogOptions options, ISerializer<TKey> keySerializer, ISerializer<TValue> valueSerializer)
  {
    Logger = logger;
    Files = files;
    DirectoryPath = directory;
    ManifestPath = Path.Combine(directory, ManifestName);
    Options = options;
    KeySerializer = keySerializer;
    ValueSerializer = valueSerializer;
    if (options.SharedModeOptions.MaximumChunkSize <= 0)
      throw new global::System.ArgumentOutOfRangeException(nameof(options), "MaximumChunkSize must be positive.");
    Files.CreateDirectory(directory);
    try
    {
      // Persist the journal directory and every ancestor link that recursive
      // creation may have added before publishing a manifest inside it.
      SyncDirectoryTree(directory);
      if (Files.FileExists(ManifestPath))
        ReadManifest();
      else
      {
        Mode = options.WriteAheadLogMode;
        StoreId = BinaryPrimitives.ReadInt64LittleEndian(Guid.NewGuid().ToByteArray());
        WriteManifest();
      }
      Recover();
      QueuedSequence = WrittenSequence;
      SyncGroup.SyncedCount = BaseSequence - 1;
      // A replacement interrupted before its end is never published. Recording
      // its abort before accepting appends also makes that decision replayable.
      foreach (var (id, channel) in Channels)
        if (channel.Pending != null)
        {
          AppendRecord(new(SharedWalRecordKind.AbortReplacement, ++QueuedSequence, id, 0, default, default));
          channel.Pending = null;
          channel.PendingStart = 0;
        }
      StartWriter();
    }
    catch
    {
      foreach (var chunk in Chunks)
        chunk.Stream?.Dispose();
      throw;
    }
  }

  internal void Register(long segmentId)
  {
#pragma warning disable CA1512 // Also targets .NET 6.
    if (segmentId <= 0)
      throw new global::System.ArgumentOutOfRangeException(nameof(segmentId));
#pragma warning restore CA1512
    lock (EnqueueLock)
    {
      ThrowIfUnavailable();
      lock (StorageLock)
      {
        var channel = GetChannel(segmentId);
        if (channel.Retired)
          throw new InvalidDataException($"Shared WAL segment {segmentId} has been retired.");
        ++HandleCount;
      }
    }
  }

  Channel GetChannel(long id)
  {
    if (!Channels.TryGetValue(id, out var channel))
      Channels.Add(id, channel = new Channel());
    return channel;
  }

  internal bool HasHistory(long id)
  {
    lock (StorageLock)
      return GetChannel(id).HasHistory;
  }

  internal (long Size, long MaximumOpIndex) GetLogicalState(long id)
  {
    long size = 0, maximum = 0;
    RunControl(() =>
    {
      lock (StorageLock)
      {
        var channel = GetChannel(id);
        size = channel.ContentSize;
        maximum = channel.MaximumOpIndex;
      }
    });
    return (size, maximum);
  }

  internal void Release()
  {
    bool close;
    lock (EnqueueLock)
    {
      close = --HandleCount == 0 && !IsClosing;
      if (close) IsClosing = true;
    }
    if (close)
      CloseCore();
  }

  void ThrowIfUnavailable()
  {
    ThrowIfFailed();
#pragma warning disable CA1513 // Also targets .NET 6.
    if (IsClosing || IsClosed)
      throw new global::System.ObjectDisposedException(nameof(SharedWriteAheadLog<TKey, TValue>));
#pragma warning restore CA1513
  }

  void ThrowIfFailed()
  {
    var failure = Failure;
    if (failure != null)
      throw new WriteAheadLogSyncFailedException(DirectoryPath, failure);
  }

  void Fail(Exception error)
  {
    Interlocked.CompareExchange(ref Failure, error, null);
    try { Logger?.LogError(error); }
    catch { /* Logging cannot strand queued sync requests. */ }
  }

  string ChunkPath(long id) => Path.Combine(DirectoryPath, id.ToString("D20", CultureInfo.InvariantCulture) + ".wal");

  CompressedFileStream OpenChunk(string path) => new(Logger, Files, path,
      Options.CompressionBlockSize, false, 0, Options.CompressionMethod, Options.CompressionLevel);

  Chunk CreateChunk(long id, long sequence)
  {
    var chunk = new Chunk
    {
      Id = id, Path = ChunkPath(id), BaseSequence = sequence,
      LastSequence = sequence, Length = SharedWalRecord.MinimumSize
    };
    chunk.Stream = OpenChunk(chunk.Path);
    new SharedWalRecord(SharedWalRecordKind.ChunkHeader, sequence, id, StoreId, default, default).Write(chunk.Stream);
    Chunks.Add(chunk);
    return chunk;
  }

  void AppendRecord(in SharedWalRecord record)
  {
    if (Current == null)
      Current = CreateChunk(FirstChunkId, WrittenSequence);
    if (Current.Sealed)
    {
      CloseInactiveChunk(Current);
      Current = CreateChunk(Current.Id + 1, WrittenSequence);
    }
    if (Current.Length > SharedWalRecord.MinimumSize &&
        Current.Length + record.Size > Options.SharedModeOptions.MaximumChunkSize)
    {
      new SharedWalRecord(SharedWalRecordKind.SealChunk, WrittenSequence, Current.Id + 1, 0, default, default).Write(Current.Stream);
      Current.Length += SharedWalRecord.MinimumSize;
      Current.Sealed = true;
      CloseInactiveChunk(Current);
      Current = CreateChunk(Current.Id + 1, WrittenSequence);
    }
    record.Write(Current.Stream);
    Current.Length += record.Size;
    Current.LastSequence = WrittenSequence = record.Sequence;
    Current.References[record.SegmentId] = record.Sequence;
    HasNewWrites = true;
  }

  static void CloseInactiveChunk(Chunk chunk)
  {
    // The sync owner also checks for a seal after releasing this lock. If it
    // is busy, hand closing to that owner rather than blocking rotation on fsync.
    if (!Monitor.TryEnter(chunk.StreamLock))
      return;
    try
    {
      // A concurrent sync can already have closed the immutable chunk.
      chunk.Stream?.CloseForReopen();
      chunk.Stream = null;
    }
    finally { Monitor.Exit(chunk.StreamLock); }
  }

  void ReadManifest()
  {
    var bytes = Files.ReadAllBytes(ManifestPath);
    if (bytes.Length != ManifestSize || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != ManifestMagic ||
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != 1 ||
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(40)) != XxHash3.HashToUInt64(bytes.AsSpan(0, 40)))
      throw new InvalidDataException("Invalid shared WAL manifest.");
    Mode = (WriteAheadLogMode)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
    if (Mode != WriteAheadLogMode.Shared && Mode != WriteAheadLogMode.AsyncShared)
      throw new NotSupportedException("Unsupported shared WAL mode.");
    StoreId = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16));
    FirstChunkId = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24));
    BaseSequence = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32));
    if (FirstChunkId <= 0 || BaseSequence < 0)
      throw new InvalidDataException("Invalid shared WAL checkpoint.");
  }

  void WriteManifest()
  {
    var bytes = new byte[ManifestSize];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, ManifestMagic);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), (int)Mode);
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), StoreId);
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), FirstChunkId);
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), BaseSequence);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), XxHash3.HashToUInt64(bytes.AsSpan(0, 40)));
    var writer = Files.GetDurableFileWriter();
    if (Files.FileExists(ManifestPath)) writer.WriteAllBytes(ManifestPath, bytes);
    else writer.WriteNewFile(ManifestPath, bytes);
    // Syncing the temporary file protects its contents, not the subsequent
    // rename. Reclamation and legacy migration may delete their fallback only
    // after the new manifest name/checkpoint is durable too.
    Files.SyncDirectory(DirectoryPath);
  }

  void SyncDirectoryTree(string path) => DirectorySync.FlushTree(path, Files.SyncDirectory);

  public void WaitForClose() => FinalSync.Task.GetAwaiter().GetResult();

  public void Dispose() => Close();

  public void Close()
  {
    lock (EnqueueLock)
    {
      if (IsClosing || IsClosed)
        return;
      IsClosing = true;
    }
    CloseCore();
  }

  void CloseCore()
  {
    Exception error = null;
    try
    {
      StopWriter();
      SyncCore(WrittenSequence);
    }
    catch (Exception e) { error = e; Fail(e); }
    finally
    {
      lock (SyncLock)
        lock (StorageLock)
          foreach (var chunk in Chunks)
            try
            {
              if (error == null) chunk.Stream?.CloseAfterSync();
              else chunk.Stream?.Dispose();
              chunk.Stream = null;
            }
            catch (Exception e) { error ??= e; Fail(e); }
      IsClosed = true;
      WakeWriter.Dispose();
      if (error == null) FinalSync.TrySetResult();
      else FinalSync.TrySetException(error);
    }
    if (error != null)
      throw new WriteAheadLogSyncFailedException(DirectoryPath, error);
  }
}
