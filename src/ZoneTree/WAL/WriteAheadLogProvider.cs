using System.Collections.Concurrent;
using System.Globalization;
using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

public sealed class WriteAheadLogProvider : IWriteAheadLogProvider
{
  internal const string LegacyWalExtension = ".wal_crc";

  internal const string Xxh3WalExtension = ".wal_xxh3";

  readonly ILogger Logger;

  readonly IFileStreamProvider FileStreamProvider;

  readonly ConcurrentDictionary<string, object> WALTable = new();

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

  public IWriteAheadLog<TKey, TValue> GetOrCreateWAL<TKey, TValue>(
      long segmentId,
      string category,
      WriteAheadLogOptions options,
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer)
  {
    if (WALTable.TryGetValue(segmentId + category, out var value))
    {
      return (IWriteAheadLog<TKey, TValue>)value;
    }

    (var walPath, var walMode) =
        DetectWalPathAndWriteAheadLogMode(segmentId, category, options);

    switch (walMode)
    {
      case WriteAheadLogMode.None:
        return new NullWriteAheadLog<TKey, TValue>();
      case WriteAheadLogMode.Sync:
        {
          var wal = new SyncFileSystemWriteAheadLog<TKey, TValue>(
              Logger,
              FileStreamProvider,
              keySerializer,
              valueSerializer,
              walPath)
          {
            EnableIncrementalBackup = options.EnableIncrementalBackup
          };
          WALTable.TryAdd(segmentId + category, wal);
          return wal;
        }
      case WriteAheadLogMode.SyncCompressed:
        {
          var wal = new SyncCompressedFileSystemWriteAheadLog<TKey, TValue>(
              Logger,
              FileStreamProvider,
              keySerializer,
              valueSerializer,
              walPath,
              options)
          {
            EnableIncrementalBackup = options.EnableIncrementalBackup
          };
          WALTable.TryAdd(segmentId + category, wal);
          return wal;
        }

      case WriteAheadLogMode.AsyncCompressed:
        {
          var wal = new AsyncCompressedFileSystemWriteAheadLog<TKey, TValue>(
              Logger,
              FileStreamProvider,
              keySerializer,
              valueSerializer,
              walPath,
              options)
          {
            EnableIncrementalBackup = options.EnableIncrementalBackup
          };
          WALTable.TryAdd(segmentId + category, wal);
          return wal;
        }
    }
    return null;
  }

  (string walPath, WriteAheadLogMode walMode)
      DetectWalPathAndWriteAheadLogMode(
      long segmentId, string category, WriteAheadLogOptions options)
  {
    var walPath = Path.Combine(WalDirectory, category, segmentId.ToString(CultureInfo.InvariantCulture));
    // Prefer an XXH3 WAL when both formats exist for the same ID.
    foreach (var extension in new[] { Xxh3WalExtension, LegacyWalExtension })
      for (var i = 0; i < 3; ++i)
      {
        var path = walPath + extension + "." + i;
        if (FileStreamProvider.FileExists(path))
          return (path, (WriteAheadLogMode)i);
      }
    return (walPath + Xxh3WalExtension + "." + (int)options.WriteAheadLogMode, options.WriteAheadLogMode);
  }

  public IWriteAheadLog<TKey, TValue> GetWAL<TKey, TValue>(long segmentId, string category)
  {
    if (WALTable.TryGetValue(segmentId + category, out var value))
    {
      return (IWriteAheadLog<TKey, TValue>)value;
    }
    return null;
  }

  public bool RemoveWAL(long segmentId, string category)
  {
    return WALTable.Remove(segmentId + category, out _);
  }

  public void DropStore()
  {
    if (FileStreamProvider.DirectoryExists(WalDirectory))
      FileStreamProvider.DeleteDirectory(WalDirectory, true);
  }

  public void InitCategory(string category)
  {
    var categoryPath = Path.Combine(WalDirectory, category);
    FileStreamProvider.CreateDirectory(categoryPath);
  }

  public void MigrateWals(Version databaseVersion)
  {
    if (databaseVersion < new Version("2.0.0"))
      RenameLegacyWals(WalDirectory);
  }

  void RenameLegacyWals(string directory)
  {
    foreach (var file in FileStreamProvider.GetFiles(directory))
    {
      var parts = Path.GetFileName(file).Split('.');
      if (parts.Length < 3 || parts[1] != "wal" ||
          !long.TryParse(parts[0], out _) || parts[2] is not ("0" or "1" or "2"))
        continue;
      var name = parts[0] + LegacyWalExtension + "." + parts[2];
      if (parts.Length > 3)
        name += "." + string.Join(".", parts.Skip(3));
      FileStreamProvider.MoveFile(file, FileStreamProvider.CombinePaths(directory, name));
    }
    foreach (var child in FileStreamProvider.GetDirectories(directory))
      RenameLegacyWals(child);
  }
}
