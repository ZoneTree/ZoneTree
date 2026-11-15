using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ZoneTree.Serializers;

/// <summary>
/// Serializes points in time as their clock ticks and their offset in minutes, padded with zeros to the size of
/// the type, as fixed-size keys and values are stored.
/// </summary>
public sealed class DateTimeOffsetSerializer : ISerializer<DateTimeOffset>
{
  public DateTimeOffset Deserialize(Memory<byte> bytes)
  {
    var span = bytes.Span;
    return new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(span),
        TimeSpan.FromMinutes(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(8))));
  }

  public Memory<byte> Serialize(in DateTimeOffset entry)
  {
    var bytes = new byte[Unsafe.SizeOf<DateTimeOffset>()];
    BinaryPrimitives.WriteInt64LittleEndian(bytes, entry.Ticks);
    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8), (short)entry.Offset.TotalMinutes);
    return bytes;
  }
}
