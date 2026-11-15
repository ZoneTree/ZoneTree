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
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, entry);
    return bytes;
  }
}
