using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes 64-bit unsigned integers as 8 bytes, little-endian.</summary>
public sealed class UInt64Serializer : ISerializer<ulong>
{
  public ulong Deserialize(Memory<byte> bytes)
    => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span);

  public Memory<byte> Serialize(in ulong entry)
  {
    var bytes = new byte[sizeof(ulong)];
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in ulong entry) => sizeof(ulong);

  public int Serialize(in ulong entry, Span<byte> destination)
  {
    BinaryPrimitives.WriteUInt64LittleEndian(destination, entry);
    return sizeof(ulong);
  }
}
