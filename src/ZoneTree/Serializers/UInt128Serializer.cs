#if NET7_0_OR_GREATER
using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes 128-bit unsigned integers as 16 bytes, little-endian.</summary>
public sealed class UInt128Serializer : ISerializer<UInt128>
{
  public UInt128 Deserialize(Memory<byte> bytes)
  {
    var span = bytes.Span;
    return new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8)), BinaryPrimitives.ReadUInt64LittleEndian(span));
  }

  public Memory<byte> Serialize(in UInt128 entry)
  {
    var bytes = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)entry);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), (ulong)(entry >> 64));
    return bytes;
  }
}
#endif
