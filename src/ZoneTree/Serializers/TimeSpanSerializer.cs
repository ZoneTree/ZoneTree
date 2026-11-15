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
    BinaryPrimitives.WriteInt64LittleEndian(bytes, entry.Ticks);
    return bytes;
  }
}
