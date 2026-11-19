using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes time intervals as the 8 bytes of their ticks, little-endian.</summary>
public sealed class TimeSpanSerializer : ISerializer<TimeSpan>
{
  public TimeSpan Deserialize(Memory<byte> bytes)
    => new(BinaryPrimitives.ReadInt64LittleEndian(bytes.Span));

  public Memory<byte> Serialize(in TimeSpan entry)
  {
    var bytes = new byte[sizeof(long)];
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in TimeSpan entry) => sizeof(long);

  public int Serialize(in TimeSpan entry, Span<byte> destination)
  {
    BinaryPrimitives.WriteInt64LittleEndian(destination, entry.Ticks);
    return sizeof(long);
  }
}
