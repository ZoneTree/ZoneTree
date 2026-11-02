using System.Buffers.Binary;
using System.IO.Hashing;
using ZoneTree.AbstractFileStream;

namespace ZoneTree.WAL;

/// <summary>
/// A fixed descriptor at the end of the tail, in unused checkpoint-sector space.
/// Its position and encoding stay the same across future tail format versions.
/// Layout: magic (8 bytes), version (4 bytes), checksum (4 bytes), little endian.
/// Version zero means no descriptor: a legacy tail or an incomplete new allocation.
/// </summary>
internal static class WalTailFormat
{
  const ulong Magic = 0x4C4941544C41575A; // ZWALTAIL

  internal const int CurrentVersion = 1;

  internal const int DescriptorSize = 16;

  internal static int ReadVersion(IFileStream stream)
  {
    if (stream.Length < DescriptorSize)
      return 0;
    stream.Position = stream.Length - DescriptorSize;
    Span<byte> bytes = stackalloc byte[DescriptorSize];
    var read = 0;
    while (read < bytes.Length)
    {
      var count = stream.Read(bytes[read..]);
      if (count == 0)
        return 0;
      read += count;
    }
    if (BinaryPrimitives.ReadUInt64LittleEndian(bytes) != Magic)
      return 0;
    if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) !=
        unchecked((uint)XxHash3.HashToUInt64(bytes[..12])))
      throw new InvalidDataException("The WAL tail format descriptor checksum is invalid.");
    var version = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
    if (version == 0)
      throw new InvalidDataException("A versioned WAL tail cannot declare legacy version zero.");
    return version;
  }

  internal static void Write(Span<byte> bytes)
  {
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, Magic);
    BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], CurrentVersion);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..],
        unchecked((uint)XxHash3.HashToUInt64(bytes[..12])));
  }
}
