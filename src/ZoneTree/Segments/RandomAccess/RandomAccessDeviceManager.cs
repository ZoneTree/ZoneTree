using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Options;

namespace ZoneTree.Segments.RandomAccess;

public sealed class RandomAccessDeviceManager : IRandomAccessDeviceManager
{
  readonly Lock SyncRoot = new();

  public ILogger Logger { get; }

  public IFileStreamProvider FileStreamProvider { get; }

  readonly Dictionary<string, IRandomAccessDevice> ReadOnlyDevices = new();

  readonly Dictionary<string, IRandomAccessDevice> WritableDevices = new();

  readonly string DataDirectory;

  public int DeviceCount => ReadOnlyDevices.Count + WritableDevices.Count;

  public int ReadOnlyDeviceCount => ReadOnlyDevices.Count;

  public int WritableDeviceCount => WritableDevices.Count;

  public RandomAccessDeviceManager(
      ILogger logger,
      IFileStreamProvider fileStreamProvider,
      string dataDirectory = "data")
  {
    Logger = logger;
    FileStreamProvider = fileStreamProvider;
    DataDirectory = dataDirectory;
    FileStreamProvider.CreateDirectory(dataDirectory);
  }

  public void CloseAllDevices()
  {
    lock (SyncRoot)
    {
      foreach (var device in ReadOnlyDevices.Values)
      {
        device.Close();
      }
      foreach (var device in WritableDevices.Values)
      {
        device.Close();
      }
    }
  }

  public void SyncDirectory() => FileStreamProvider.SyncDirectory(DataDirectory);

  public IRandomAccessDevice CreateWritableDevice(
      long segmentId, string category,
      bool isCompressed, int compressionBlockSize,
      bool deleteIfExists, bool backupIfDelete,
      CompressionMethod compressionMethod,
      int compressionLevel)
  {
    lock (SyncRoot)
    {
      var key = GetDeviceKey(segmentId, category);
      if (WritableDevices.ContainsKey(key))
      {
        throw new Exception($"Writable device can be created only once. segmentId: {segmentId:X} category: {category}");
      }
      var filePath = GetFilePath(segmentId, category);
      if (deleteIfExists) DeleteFileIfExists(backupIfDelete, filePath);
      if (isCompressed)
      {
        filePath += ".z";
      }
      if (deleteIfExists) DeleteFileIfExists(backupIfDelete, filePath);
      IRandomAccessDevice device = isCompressed ?
          new CompressedFileRandomAccessDevice(
              Logger,
              FileStreamProvider,
              segmentId, category, this, filePath, true,
              compressionBlockSize,
              compressionMethod,
              compressionLevel) :
          new FileRandomAccessDevice(
              FileStreamProvider,
              segmentId, category, this, filePath, true);
      WritableDevices.Add(key, device);
      return device;
    }
  }

  void DeleteFileIfExists(bool backupIfDelete, string filePath)
  {
    if (!FileStreamProvider.FileExists(filePath))
      return;
    if (backupIfDelete)
      FileStreamProvider.Replace(filePath,
          filePath + ".backup." + Guid.NewGuid().ToString("N"), null);
    else
      FileStreamProvider.DeleteFile(filePath);
  }

  public IReadOnlyList<IRandomAccessDevice> GetDevices()
  {
    var a = GetReadOnlyDevices();
    var b = GetWritableDevices();
    return a.Concat(b).ToArray();
  }

  static string GetDeviceKey(long segmentId, string category)
  {
    return segmentId + category;
  }

  public string GetFilePath(long segmentId, string category)
  {
    return Path.Combine(DataDirectory, segmentId + category);
  }

  public IRandomAccessDevice GetReadOnlyDevice(
      long segmentId, string category,
      bool isCompressed, int compressionBlockSize,
      CompressionMethod compressionMethod,
      int compressionLevel)
  {
    lock (SyncRoot)
    {
      var key = GetDeviceKey(segmentId, category);
      if (ReadOnlyDevices.TryGetValue(key, out var device))
        return device;

      if (WritableDevices.ContainsKey(key))
      {
        throw new Exception($"ReadOnly device can be created after writable device is closed. segmentId: {segmentId} category: {category}");
      }
      var filePath = GetFilePath(segmentId, category);
      if (isCompressed && FileStreamProvider.FileExists(filePath))
      {
        isCompressed = false;
      }
      var compressedfilePath = filePath + ".z";
      if (!isCompressed && FileStreamProvider.FileExists(compressedfilePath))
      {
        isCompressed = true;
      }

      if (isCompressed)
      {
        filePath = compressedfilePath;
      }

      device = isCompressed ?
          new CompressedFileRandomAccessDevice(
              Logger,
              FileStreamProvider,
              segmentId, category, this, filePath, false,
              compressionBlockSize,
              compressionMethod,
              compressionLevel) :
          new FileRandomAccessDevice(
              FileStreamProvider,
              segmentId, category, this, filePath, false);
      ReadOnlyDevices.Add(key, device);
      return device;
    }
  }

  public IReadOnlyList<IRandomAccessDevice> GetReadOnlyDevices()
  {
    lock (SyncRoot)
    {
      return ReadOnlyDevices.Values.ToArray();
    }
  }

  public IReadOnlyList<IRandomAccessDevice> GetWritableDevices()
  {
    lock (SyncRoot)
    {
      return WritableDevices.Values.ToArray();
    }
  }

  public void RemoveReadOnlyDevice(long segmentId, string category)
  {
    lock (SyncRoot)
    {
      var key = GetDeviceKey(segmentId, category);
      ReadOnlyDevices.Remove(key);
    }
  }

  public void RemoveWritableDevice(long segmentId, string category)
  {
    lock (SyncRoot)
    {
      var key = GetDeviceKey(segmentId, category);
      WritableDevices.Remove(key);
    }
  }

  public bool DeviceExists(long segmentId, string category, bool isCompressed)
  {
    lock (SyncRoot)
    {
      var filePath = GetFilePath(segmentId, category);
      if (isCompressed) filePath += ".z";
      return FileStreamProvider.FileExists(filePath);
    }
  }

  public void DeleteDevice(long segmentId, string category, bool isCompressed)
  {
    lock (SyncRoot)
    {
      var filePath = GetFilePath(segmentId, category);
      if (isCompressed) filePath += ".z";
      if (FileStreamProvider.FileExists(filePath))
        FileStreamProvider.DeleteFile(filePath);
    }
  }

  public void DropStore()
  {
    lock (SyncRoot)
    {
      if (FileStreamProvider.DirectoryExists(DataDirectory))
        FileStreamProvider.DeleteDirectory(DataDirectory, true);
    }
  }
}
