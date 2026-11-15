using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes DateTime values as the 8 bytes of their ticks, little-endian. The kind is not kept.</summary>
public sealed class DateTimeSerializer : ISerializer<DateTime>
{
  public DateTime Deserialize(Memory<byte> bytes)
    => new(BinaryPrimitives.ReadInt64LittleEndian(bytes.Span));

  public Memory<byte> Serialize(in DateTime entry)
  {
    var bytes = new byte[sizeof(long)];
    BinaryPrimitives.WriteInt64LittleEndian(bytes, entry.Ticks);
    return bytes;
  }
}
