using ZoneTree.AbstractFileStream;
using ZoneTree.Options;

namespace ZoneTree.Segments.RandomAccess;

public interface IRandomAccessDeviceManager
{
  IFileStreamProvider FileStreamProvider { get; }

  IRandomAccessDevice GetReadOnlyDevice(
      long segmentId, string category, bool isCompressed,
      int compressionBlockSize,
      CompressionMethod compressionMethod,
      int compressionLevel);

  IRandomAccessDevice CreateWritableDevice(
      long segmentId, string category, bool isCompressed,
      int compressionBlockSize,
      bool deleteIfExists, bool backupIfDelete,
      CompressionMethod compressionMethod,
      int compressionLevel);

  bool DeviceExists(long segmentId, string category, bool isCompressed);

  void DeleteDevice(long segmentId, string category, bool isCompressed);

  int DeviceCount { get; }

  int ReadOnlyDeviceCount { get; }

  int WritableDeviceCount { get; }

  IReadOnlyList<IRandomAccessDevice> GetReadOnlyDevices();

  IReadOnlyList<IRandomAccessDevice> GetWritableDevices();

  IReadOnlyList<IRandomAccessDevice> GetDevices();

  void CloseAllDevices();

  /// <summary>
  /// Makes newly created device names and metadata replacements durable before
  /// publishing a checkpoint or deleting its recovery fallback. Custom managers
  /// with devices in multiple directories must sync every affected directory.
  /// </summary>
  void SyncDirectory();

  void RemoveReadOnlyDevice(long segmentId, string category);

  void RemoveWritableDevice(long segmentId, string category);

  void DropStore();

  string GetFilePath(long segmentId, string category);
}
