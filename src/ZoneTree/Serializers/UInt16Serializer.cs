using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes 16-bit unsigned integers as 2 bytes, little-endian.</summary>
public sealed class UInt16Serializer : ISerializer<ushort>
{
  public ushort Deserialize(Memory<byte> bytes)
    => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Span);

  public Memory<byte> Serialize(in ushort entry)
  {
    var bytes = new byte[sizeof(ushort)];
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in ushort entry) => sizeof(ushort);

  public int Serialize(in ushort entry, Span<byte> destination)
  {
    BinaryPrimitives.WriteUInt16LittleEndian(destination, entry);
    return sizeof(ushort);
  }
}
