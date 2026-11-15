namespace ZoneTree.Serializers;

/// <summary>Serializes signed bytes as 1 byte.</summary>
public sealed class SByteSerializer : ISerializer<sbyte>
{
  public sbyte Deserialize(Memory<byte> bytes)
    => (sbyte)bytes.Span[0];

  public Memory<byte> Serialize(in sbyte entry)
    => new byte[] { (byte)entry };
}
