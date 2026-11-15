using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes doubles as their 8 IEEE 754 bytes, little-endian.</summary>
public sealed class DoubleSerializer : ISerializer<double>
{
  public double Deserialize(Memory<byte> bytes)
    => BinaryPrimitives.ReadDoubleLittleEndian(bytes.Span);

  public Memory<byte> Serialize(in double entry)
  {
    var bytes = new byte[sizeof(double)];
    BinaryPrimitives.WriteDoubleLittleEndian(bytes, entry);
    return bytes;
  }
}
