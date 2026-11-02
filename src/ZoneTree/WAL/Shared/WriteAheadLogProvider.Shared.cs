using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

public sealed partial class WriteAheadLogProvider
{
  readonly Lock SharedStoresLock = new();
  readonly Dictionary<string, ISharedWalStore> SharedStores = new();

  IWriteAheadLog<TKey, TValue> GetSharedWAL<TKey, TValue>(long id, string category,
      WriteAheadLogOptions options, ISerializer<TKey> keys, ISerializer<TValue> values)
  {
    lock (SharedStoresLock)
    {
      if (WALTable.TryGetValue(id + category, out var existing))
        return (IWriteAheadLog<TKey, TValue>)existing;
      if (SharedStores.TryGetValue(category, out var previous) && previous.IsClosing)
      {
        previous.WaitForClose();
        SharedStores.Remove(category);
      }
      SharedWalStore<TKey, TValue> store;
      if (SharedStores.TryGetValue(category, out previous))
        store = (SharedWalStore<TKey, TValue>)previous;
      else
      {
        try
        {
          store = new(Logger, FileStreamProvider,
              Path.Combine(WalDirectory, category, SharedWalStore<TKey, TValue>.DirectoryName), options, keys, values);
        }
        catch (InvalidDataException e)
        {
          throw new WriteAheadLogCorruptionException(id, new() { [0] = e });
        }
        SharedStores.Add(category, store);
      }
      SharedWriteAheadLog<TKey, TValue> handle = null;
      try
      {
        handle = new SharedWriteAheadLog<TKey, TValue>(store, id, keys, values);
        // The append-only replacement end record is durable before deleting a
        // legacy WAL. An interrupted migration can safely use the old file again.
        var (path, mode) = DetectWalPathAndWriteAheadLogMode(id, category, options);
        if (FileStreamProvider.FileExists(path))
        {
          using IWriteAheadLog<TKey, TValue> legacy = mode switch
          {
            WriteAheadLogMode.Sync => new SyncFileSystemWriteAheadLog<TKey, TValue>(Logger, FileStreamProvider, keys, values, path),
            WriteAheadLogMode.SyncCompressed => new SyncCompressedFileSystemWriteAheadLog<TKey, TValue>(Logger, FileStreamProvider, keys, values, path, options),
            WriteAheadLogMode.AsyncCompressed => new AsyncCompressedFileSystemWriteAheadLog<TKey, TValue>(Logger, FileStreamProvider, keys, values, path, options),
            _ => throw new InvalidDataException("Invalid legacy WAL mode.")
          };
          if (!store.HasHistory(id))
          {
            var read = legacy.ReadLogEntries(true, true, true);
            if (!read.Success && !read.HasFoundIncompleteTailRecord)
              throw new WriteAheadLogCorruptionException(id, read.Exceptions);
            store.Replace(id, read.Keys.ToArray(), read.Values.ToArray(), read.MaximumOpIndex);
          }
          // Recovery can observe a completed replacement before its original
          // sync acknowledgement. Sync it again before removing the fallback.
          store.Sync();
          legacy.Drop();
        }
        WALTable.TryAdd(id + category, handle);
        var result = handle;
        handle = null; // The provider and owning segment now own the handle.
        return result;
      }
      finally
      {
        handle?.Dispose();
      }
    }
  }

}
