using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes 16-bit signed integers as 2 bytes, little-endian.</summary>
public sealed class Int16Serializer : ISerializer<short>
{
  public short Deserialize(Memory<byte> bytes)
    => BinaryPrimitives.ReadInt16LittleEndian(bytes.Span);

  public Memory<byte> Serialize(in short entry)
  {
    var bytes = new byte[sizeof(short)];
    BinaryPrimitives.WriteInt16LittleEndian(bytes, entry);
    return bytes;
  }
}
