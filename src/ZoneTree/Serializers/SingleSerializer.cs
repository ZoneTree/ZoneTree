using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes floats as their 4 IEEE 754 bytes, little-endian.</summary>
public sealed class SingleSerializer : ISerializer<float>
{
  public float Deserialize(Memory<byte> bytes)
    => BinaryPrimitives.ReadSingleLittleEndian(bytes.Span);

  public Memory<byte> Serialize(in float entry)
  {
    var bytes = new byte[sizeof(float)];
    BinaryPrimitives.WriteSingleLittleEndian(bytes, entry);
    return bytes;
  }
}
