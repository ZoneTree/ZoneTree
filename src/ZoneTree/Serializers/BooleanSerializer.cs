namespace ZoneTree.Serializers;

public sealed class BooleanSerializer : ISerializer<bool>
{
  public bool Deserialize(Memory<byte> bytes)
  {
    return BitConverter.ToBoolean(bytes.Span);
  }

  public Memory<byte> Serialize(in bool entry)
  {
    return BitConverter.GetBytes(entry);
  }

  public int GetMaxByteCount(in bool entry) => sizeof(bool);

  // The same byte BitConverter.GetBytes writes: 1 for true and 0 for false.
  public int Serialize(in bool entry, Span<byte> destination)
  {
    destination[0] = entry ? (byte)1 : (byte)0;
    return sizeof(bool);
  }
}
