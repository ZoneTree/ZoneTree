using System.Buffers.Binary;
using System.IO.Hashing;
using ZoneTree.AbstractFileStream;

namespace ZoneTree.WAL;

/// <summary>
/// A recovery hint naming an append-only main-file prefix whose disk sync
/// completed before this hint was written. It need not persist to acknowledge
/// the main-file data; framed blocks also recover without a checkpoint.
/// Two fixed slots in the tail file, on separate sectors. Updating one leaves
/// the preceding checkpoint intact if a power loss tears the update.
/// </summary>
internal static class WalSyncCheckpoint
{
  const ulong Magic = 0x315043534C41575A; // ZWALSCP1

  public const int SlotSize = 512;

  public const int RegionSize = 2 * SlotSize;

  const int Size = 40;

  public static (long MainLength, long ContentLength)? Read(IFileStream stream, long offset, out long sequence)
  {
    sequence = 0;
    (long, long)? result = null;
    Span<byte> bytes = stackalloc byte[Size];
    for (var slot = 0; slot < 2; ++slot)
    {
      stream.Position = offset + slot * SlotSize;
      var read = 0;
      while (read < Size)
      {
        var count = stream.Read(bytes[read..]);
        if (count == 0)
          break;
        read += count;
      }
      if (read != Size || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != Magic ||
          BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]) != XxHash3.HashToUInt64(bytes[..32]))
        continue;
      var candidate = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
      var mainLength = BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]);
      var contentLength = BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]);
      if (candidate > sequence && mainLength >= 1 && contentLength >= 0)
      {
        sequence = candidate;
        result = (mainLength, contentLength);
      }
    }
    return result;
  }

  public static void Write(IFileStream stream, long offset, long sequence, long mainLength, long contentLength)
  {
    Span<byte> bytes = stackalloc byte[Size];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, Magic);
    BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], sequence);
    BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], mainLength);
    BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], contentLength);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], XxHash3.HashToUInt64(bytes[..32]));
    stream.Position = offset + (sequence % 2) * SlotSize;
    stream.Write(bytes);
  }
}
