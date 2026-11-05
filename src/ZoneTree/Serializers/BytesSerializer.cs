namespace ZoneTree.Serializers;

/// <summary>
/// Serializes byte arrays as their bytes. A null array serializes as no bytes, and reads back
/// as an empty array.
/// </summary>
public sealed class BytesSerializer : ISerializer<byte[]>
{
  public byte[] Deserialize(Memory<byte> bytes)
  {
    // A copy, so the array does not hold on to a block cache.
    return bytes.ToArray();
  }

  public Memory<byte> Serialize(in byte[] entry)
  {
    return entry;
  }
}
