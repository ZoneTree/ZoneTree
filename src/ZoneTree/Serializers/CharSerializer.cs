using System.Buffers.Binary;

namespace ZoneTree.Serializers;

/// <summary>Serializes chars as the 2 bytes of their UTF-16 code unit, little-endian.</summary>
public sealed class CharSerializer : ISerializer<char>
{
  public char Deserialize(Memory<byte> bytes)
    => (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.Span);

  public Memory<byte> Serialize(in char entry)
  {
    var bytes = new byte[sizeof(char)];
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in char entry) => sizeof(char);

  public int Serialize(in char entry, Span<byte> destination)
  {
    BinaryPrimitives.WriteUInt16LittleEndian(destination, entry);
    return sizeof(char);
  }
}
