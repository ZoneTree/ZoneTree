using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes 32-bit unsigned integers as 4 bytes, little-endian.</summary>
public sealed class UInt32Serializer : ISerializer<uint>
{
  public uint Deserialize(Memory<byte> bytes)
    => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span);

  public Memory<byte> Serialize(in uint entry)
  {
    var bytes = new byte[sizeof(uint)];
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in uint entry) => sizeof(uint);

  public int Serialize(in uint entry, Span<byte> destination)
  {
    BinaryPrimitives.WriteUInt32LittleEndian(destination, entry);
    return sizeof(uint);
  }
}
