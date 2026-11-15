using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes 32-bit signed integers as 4 bytes, little-endian.</summary>
public sealed class Int32Serializer : ISerializer<int>
{
  public int Deserialize(Memory<byte> bytes)
    => BinaryPrimitives.ReadInt32LittleEndian(bytes.Span);

  public Memory<byte> Serialize(in int entry)
  {
    var bytes = new byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, entry);
    return bytes;
  }
}
