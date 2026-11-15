using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes 64-bit unsigned integers as 8 bytes, little-endian.</summary>
public sealed class UInt64Serializer : ISerializer<ulong>
{
  public ulong Deserialize(Memory<byte> bytes)
    => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span);

  public Memory<byte> Serialize(in ulong entry)
  {
    var bytes = new byte[sizeof(ulong)];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, entry);
    return bytes;
  }
}
