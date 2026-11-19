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
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in Half entry) => sizeof(short);

  public int Serialize(in Half entry, Span<byte> destination)
  {
    BinaryPrimitives.WriteInt16LittleEndian(destination, BitConverter.HalfToInt16Bits(entry));
    return sizeof(short);
  }
}
