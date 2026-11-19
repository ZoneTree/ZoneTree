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
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in float entry) => sizeof(float);

  public int Serialize(in float entry, Span<byte> destination)
  {
    BinaryPrimitives.WriteSingleLittleEndian(destination, entry);
    return sizeof(float);
  }
}
