using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ZoneTree.Serializers;

/// <summary>
/// Serializes decimals as 16 bytes, little-endian: the sign and scale, the high 32 bits of the coefficient, then
/// its low 64 bits, the layout .NET and the Win32 DECIMAL structure share.
/// </summary>
public sealed class DecimalSerializer : ISerializer<decimal>
{
  /// <exception cref="ArgumentOutOfRangeException">The bytes are fewer than a decimal's 16.</exception>
  public decimal Deserialize(Memory<byte> bytes)
  {
    var span = bytes.Span;
    // On a little-endian machine a decimal's memory is already in that layout.
    if (BitConverter.IsLittleEndian)
      return MemoryMarshal.Read<decimal>(span);
    var flags = BinaryPrimitives.ReadInt32LittleEndian(span);
    return new decimal(
        BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8)),
        BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12)),
        BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4)),
        flags < 0,
        (byte)(flags >> 16));
  }

  public Memory<byte> Serialize(in decimal entry)
  {
    var bytes = new byte[sizeof(decimal)];
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in decimal entry) => sizeof(decimal);

  public int Serialize(in decimal entry, Span<byte> destination)
  {
    if (BitConverter.IsLittleEndian)
    {
      Unsafe.WriteUnaligned(ref destination[0], entry);
      return sizeof(decimal);
    }
    Span<int> bits = stackalloc int[4];
    decimal.GetBits(entry, bits);
    BinaryPrimitives.WriteInt32LittleEndian(destination, bits[3]);
    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4), bits[2]);
    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(8), bits[0]);
    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(12), bits[1]);
    return sizeof(decimal);
  }
}
