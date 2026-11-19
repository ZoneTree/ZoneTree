using System.IO;
using System.Reflection.PortableExecutable;
using ZoneTree.AbstractFileStream;
using ZoneTree.Segments.Block;

namespace ZoneTree.Segments.RandomAccess;

public sealed class FileRandomAccessDevice : IRandomAccessDevice
{
  readonly Lock SyncRoot = new();

  readonly string Category;

  IFileStream FileStream;

  readonly IFileStreamProvider FileStreamProvider;

  readonly IRandomAccessDeviceManager RandomDeviceManager;

  public string FilePath { get; }

  public long SegmentId { get; }

  public bool Writable { get; }

  public long Length => FileStream.Length;

  public int ReadBufferCount => 0;

  public FileRandomAccessDevice(
      IFileStreamProvider fileStreamProvider,
      long segmentId,
      string category,
      IRandomAccessDeviceManager randomDeviceManager,
      string filePath, bool writable, int fileIOBufferSize = 4096)
  {
    FileStreamProvider = fileStreamProvider;
    SegmentId = segmentId;
    Category = category;
    RandomDeviceManager = randomDeviceManager;
    FilePath = filePath;
    Writable = writable;
    var fileMode = writable ? FileMode.OpenOrCreate : FileMode.Open;
    var fileAccess = writable ? FileAccess.ReadWrite : FileAccess.Read;
    var fileShare = FileShare.Read;
    FileStream = fileStreamProvider.CreateFileStream(filePath,
        fileMode,
        fileAccess,
        fileShare, fileIOBufferSize);
    if (writable)
    {
      FileStream.Seek(0, SeekOrigin.End);
    }
  }

  public long AppendBytesReturnPosition(ReadOnlySpan<byte> bytes)
  {
    var pos = FileStream.Position;
    FileStream.Write(bytes);
    FileStream.Flush(true);
    return pos;
  }

  public Memory<byte> GetBytes(long offset, int length, SingleBlockPin blockPin)
  {
    lock (SyncRoot)
    {
      var bytes = new byte[length];
      FileStream.Seek(offset, SeekOrigin.Begin);
      FileStream.ReadFaster(bytes, 0, length);
      return bytes;
    }
  }

  public void Dispose()
  {
    if (FileStream == null)
      return;
    Close();
  }

  public void Close()
  {
    if (FileStream == null)
      return;
    FileStream.Flush(true);
    FileStream.Dispose();
    FileStream = null;
    if (Writable)
      RandomDeviceManager.RemoveWritableDevice(SegmentId, Category);
    else
      RandomDeviceManager.RemoveReadOnlyDevice(SegmentId, Category);
  }

  public void Delete()
  {
    Dispose();
    FileStreamProvider.DeleteFile(FilePath);
  }

  public void ClearContent()
  {
    FileStream.SetLength(0);
    FileStream.Seek(0, SeekOrigin.Begin);
  }

  public void SealDevice()
  {
    // nothing here.
  }

  public int ReleaseInactiveCachedBuffers(long ticks)
  {
    // no buffer
    return 0;
  }
}
