namespace ZoneTree.Serializers;

/// <summary>Serializes signed bytes as 1 byte.</summary>
public sealed class SByteSerializer : ISerializer<sbyte>
{
  public sbyte Deserialize(Memory<byte> bytes)
    => (sbyte)bytes.Span[0];

  public Memory<byte> Serialize(in sbyte entry)
    => new byte[] { (byte)entry };

  public int GetMaxByteCount(in sbyte entry) => 1;

  public int Serialize(in sbyte entry, Span<byte> destination)
  {
    destination[0] = (byte)entry;
    return 1;
  }
}
