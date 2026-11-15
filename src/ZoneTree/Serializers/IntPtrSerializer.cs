using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>
/// Serializes native-sized signed integers in their native size, little-endian: 8 bytes in a 64-bit process and
/// 4 in a 32-bit one, so a process of the same size reads them.
/// </summary>
public sealed class IntPtrSerializer : ISerializer<nint>
{
  public nint Deserialize(Memory<byte> bytes)
    => IntPtr.Size == sizeof(long)
        ? (nint)BinaryPrimitives.ReadInt64LittleEndian(bytes.Span)
        : BinaryPrimitives.ReadInt32LittleEndian(bytes.Span);

  public Memory<byte> Serialize(in nint entry)
  {
    var bytes = new byte[IntPtr.Size];
    if (IntPtr.Size == sizeof(long))
      BinaryPrimitives.WriteInt64LittleEndian(bytes, entry);
    else
      BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)entry);
    return bytes;
  }
}
