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
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in long entry) => sizeof(long);

  public int Serialize(in long entry, Span<byte> destination)
  {
    BinaryPrimitives.WriteInt64LittleEndian(destination, entry);
    return sizeof(long);
  }
}
