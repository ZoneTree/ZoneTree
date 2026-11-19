using System.Collections.Concurrent;
using System.Globalization;
using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

public sealed partial class WriteAheadLogProvider : IWriteAheadLogProvider
{
  internal const string Xxh3WalExtension = ".wal_xxh3";

  readonly ILogger Logger;

  readonly IFileStreamProvider FileStreamProvider;

  readonly ConcurrentDictionary<string, object> WalTable = new();

  public string WalDirectory { get; }

  public WriteAheadLogProvider(
      ILogger logger,
      IFileStreamProvider fileStreamProvider,
      string walDirectory = "data")
  {
    Logger = logger;
    FileStreamProvider = fileStreamProvider;
    WalDirectory = walDirectory;
    FileStreamProvider.CreateDirectory(walDirectory);
  }

  public IWriteAheadLog<TKey, TValue> GetOrCreateWal<TKey, TValue>(
      long segmentId,
      string category,
      WriteAheadLogOptions options,
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer)
  {
    if (WalTable.TryGetValue(segmentId + category, out var value))
    {
      return (IWriteAheadLog<TKey, TValue>)value;
    }

    if (options.Mode is WriteAheadLogMode.Shared or WriteAheadLogMode.AsyncShared ||
        FileStreamProvider.FileExists(Path.Combine(WalDirectory, category, SharedWalStore<TKey, TValue>.DirectoryName, SharedWalStore<TKey, TValue>.ManifestName)))
      return GetSharedWal(segmentId, category, options, keySerializer, valueSerializer);

    (var walPath, var walMode) =
        DetectWalPathAndWriteAheadLogMode(segmentId, category, options);

    try
    {
      switch (walMode)
      {
        case WriteAheadLogMode.None:
          return new NullWriteAheadLog<TKey, TValue>();
        case WriteAheadLogMode.Private:
          {
            var wal = new PrivateWriteAheadLog<TKey, TValue>(
                Logger,
                FileStreamProvider,
                keySerializer,
                valueSerializer,
                walPath,
                options);
            WalTable.TryAdd(segmentId + category, wal);
            return wal;
          }

        case WriteAheadLogMode.AsyncPrivate:
          {
            var wal = new AsyncPrivateWriteAheadLog<TKey, TValue>(
                Logger,
                FileStreamProvider,
                keySerializer,
                valueSerializer,
                walPath,
                options);
            WalTable.TryAdd(segmentId + category, wal);
            return wal;
          }
      }
    }
    catch (InvalidDataException e)
    {
      throw new WriteAheadLogCorruptionException(segmentId, new() { [0] = e });
    }
    return null;
  }

  (string walPath, WriteAheadLogMode walMode)
      DetectWalPathAndWriteAheadLogMode(
      long segmentId, string category, WriteAheadLogOptions options)
  {
    var walPath = Path.Combine(WalDirectory, category, segmentId.ToString(CultureInfo.InvariantCulture));
    foreach (var mode in new[] { WriteAheadLogMode.Private, WriteAheadLogMode.AsyncPrivate })
    {
      var path = walPath + Xxh3WalExtension + "." + (int)mode;
      if (FileStreamProvider.FileExists(path))
        return (path, mode);
    }
    return (walPath + Xxh3WalExtension + "." + (int)options.Mode, options.Mode);
  }

  public IWriteAheadLog<TKey, TValue> GetWal<TKey, TValue>(long segmentId, string category)
  {
    if (WalTable.TryGetValue(segmentId + category, out var value))
    {
      return (IWriteAheadLog<TKey, TValue>)value;
    }
    return null;
  }

  public bool RemoveWal(long segmentId, string category)
  {
    return WalTable.Remove(segmentId + category, out _);
  }

  public void DropStore()
  {
    lock (SharedStoresLock)
      foreach (var store in SharedStores.Values)
        store.Close();
    if (FileStreamProvider.DirectoryExists(WalDirectory))
      FileStreamProvider.DeleteDirectory(WalDirectory, true);
  }

  public void InitCategory(string category)
  {
    var categoryPath = Path.Combine(WalDirectory, category);
    FileStreamProvider.CreateDirectory(categoryPath);
  }

  // Restore creates closed WALs before publishing database metadata. Persist
  // their names too, including a separately configured WAL directory.
  internal void SyncCategoryDirectory(string category)
      => DirectorySync.FlushTree(Path.Combine(WalDirectory, category), FileStreamProvider.SyncDirectory);
}
