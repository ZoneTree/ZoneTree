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
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in short entry) => sizeof(short);

  public int Serialize(in short entry, Span<byte> destination)
  {
    BinaryPrimitives.WriteInt16LittleEndian(destination, entry);
    return sizeof(short);
  }
}
