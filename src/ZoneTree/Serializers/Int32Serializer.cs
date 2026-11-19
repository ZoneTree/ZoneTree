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
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in int entry) => sizeof(int);

  public int Serialize(in int entry, Span<byte> destination)
  {
    BinaryPrimitives.WriteInt32LittleEndian(destination, entry);
    return sizeof(int);
  }
}
