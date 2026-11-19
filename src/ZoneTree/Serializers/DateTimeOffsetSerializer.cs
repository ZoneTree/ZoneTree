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
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in DateTimeOffset entry) => Unsafe.SizeOf<DateTimeOffset>();

  public int Serialize(in DateTimeOffset entry, Span<byte> destination)
  {
    // The ticks and the offset fill 10 of the 16 bytes. A new array leaves the rest zero, and a reused buffer must
    // write them as zero too.
    destination.Slice(0, Unsafe.SizeOf<DateTimeOffset>()).Clear();
    BinaryPrimitives.WriteInt64LittleEndian(destination, entry.Ticks);
    BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(8), (short)entry.Offset.TotalMinutes);
    return Unsafe.SizeOf<DateTimeOffset>();
  }
}
