using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>
/// Serializes native-sized unsigned integers in their native size, little-endian: 8 bytes in a 64-bit process and
/// 4 in a 32-bit one, so a process of the same size reads them.
/// </summary>
public sealed class UIntPtrSerializer : ISerializer<nuint>
{
  public nuint Deserialize(Memory<byte> bytes)
    => UIntPtr.Size == sizeof(ulong)
        ? (nuint)BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span)
        : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span);

  public Memory<byte> Serialize(in nuint entry)
  {
    var bytes = new byte[UIntPtr.Size];
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in nuint entry) => UIntPtr.Size;

  public int Serialize(in nuint entry, Span<byte> destination)
  {
    if (UIntPtr.Size == sizeof(ulong))
      BinaryPrimitives.WriteUInt64LittleEndian(destination, entry);
    else
      BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)entry);
    return UIntPtr.Size;
  }
}
