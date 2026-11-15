using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes dates as the 4 bytes of their day number, little-endian.</summary>
public sealed class DateOnlySerializer : ISerializer<DateOnly>
{
  public DateOnly Deserialize(Memory<byte> bytes)
    => DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(bytes.Span));

  public Memory<byte> Serialize(in DateOnly entry)
  {
    var bytes = new byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, entry.DayNumber);
    return bytes;
  }
}
