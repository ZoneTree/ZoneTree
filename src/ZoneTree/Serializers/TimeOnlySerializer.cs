using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes times of day as the 8 bytes of their ticks, little-endian.</summary>
public sealed class TimeOnlySerializer : ISerializer<TimeOnly>
{
  public TimeOnly Deserialize(Memory<byte> bytes)
    => new(BinaryPrimitives.ReadInt64LittleEndian(bytes.Span));

  public Memory<byte> Serialize(in TimeOnly entry)
  {
    var bytes = new byte[sizeof(long)];
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in TimeOnly entry) => sizeof(long);

  public int Serialize(in TimeOnly entry, Span<byte> destination)
  {
    BinaryPrimitives.WriteInt64LittleEndian(destination, entry.Ticks);
    return sizeof(long);
  }
}
