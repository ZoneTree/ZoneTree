namespace ZoneTree.Serializers;

public sealed class ByteSerializer : ISerializer<byte>
{
  public byte Deserialize(Memory<byte> bytes)
  {
    return bytes.Span[0];
  }

  public Memory<byte> Serialize(in byte entry)
  {
    return new byte[1] { entry };
  }

  public int GetMaxByteCount(in byte entry) => 1;

  public int Serialize(in byte entry, Span<byte> destination)
  {
    destination[0] = entry;
    return 1;
  }
}
