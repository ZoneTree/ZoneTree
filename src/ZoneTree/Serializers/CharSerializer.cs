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
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, entry);
    return bytes;
  }
}
