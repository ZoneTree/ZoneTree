using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes 64-bit signed integers as 8 bytes, little-endian.</summary>
public sealed class Int64Serializer : ISerializer<long>
{
  public long Deserialize(Memory<byte> bytes)
    => BinaryPrimitives.ReadInt64LittleEndian(bytes.Span);

  public Memory<byte> Serialize(in long entry)
  {
    var bytes = new byte[sizeof(long)];
    BinaryPrimitives.WriteInt64LittleEndian(bytes, entry);
    return bytes;
  }
}
