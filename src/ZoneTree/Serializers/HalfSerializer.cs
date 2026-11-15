using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes half-precision floats as their 2 IEEE 754 bytes, little-endian.</summary>
public sealed class HalfSerializer : ISerializer<Half>
{
  public Half Deserialize(Memory<byte> bytes)
    => BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(bytes.Span));

  public Memory<byte> Serialize(in Half entry)
  {
    var bytes = new byte[sizeof(short)];
    BinaryPrimitives.WriteInt16LittleEndian(bytes, BitConverter.HalfToInt16Bits(entry));
    return bytes;
  }
}
