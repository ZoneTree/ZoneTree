using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace ZoneTree.WAL;

/// <summary>
/// A tail file mapped into memory, with the tail block bytes in the mapping.
/// A store into the mapping is in the operating system's page cache at once,
/// so it survives a crash of the process without any system call.
/// </summary>
internal sealed unsafe class MappedTailFile : MemoryManager<byte>
{
  public const int HeaderSize = 2 * sizeof(int);

  readonly MemoryMappedFile MappedFile;

  readonly MemoryMappedViewAccessor View;

  readonly byte* FileStart;

  readonly int Capacity;

  /// <summary>
  /// The start of the tail block bytes.
  /// </summary>
  public byte* Data { get; }

  /// <param name="fileStream">The tail file. It stays open and it must
  /// be longer than the capacity by the size of the header.</param>
  /// <param name="capacity">The capacity of the tail block.</param>
  public MappedTailFile(FileStream fileStream, int capacity)
  {
    Capacity = capacity;
    MappedFile = MemoryMappedFile.CreateFromFile(
        fileStream,
        null,
        HeaderSize + capacity,
        MemoryMappedFileAccess.ReadWrite,
        HandleInheritability.None,
        true);
    View = MappedFile.CreateViewAccessor(0, HeaderSize + capacity, MemoryMappedFileAccess.ReadWrite);
    byte* pointer = null;
    View.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
    FileStart = pointer;
    Data = pointer + HeaderSize;
  }

  /// <summary>
  /// Makes the block index and the block length visible to a later load.
  /// A single aligned store cannot be torn by a crash, and it happens
  /// after the stores of the block bytes that the new length covers.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void Publish(int blockIndex, int length)
  {
    Volatile.Write(
        ref Unsafe.AsRef<long>(FileStart),
        (uint)blockIndex | ((long)length << 32));
  }

  /// <summary>
  /// Hands the dirty pages to the file system.
  /// The file still needs a disk sync after this call.
  /// </summary>
  public void Flush()
  {
    View.Flush();
  }

  public override Span<byte> GetSpan()
  {
    return new Span<byte>(Data, Capacity);
  }

  public override MemoryHandle Pin(int elementIndex = 0)
  {
    return new MemoryHandle(Data + elementIndex);
  }

  public override void Unpin()
  {
  }

  protected override void Dispose(bool disposing)
  {
    if (!disposing)
      return;
    View.SafeMemoryMappedViewHandle.ReleasePointer();
    View.Dispose();
    MappedFile.Dispose();
  }
}
