
using System.Runtime.CompilerServices;

namespace ZoneTree.Serializers;

public sealed class ByteArraySerializer : ISerializer<Memory<byte>>
{
  public Memory<byte> Deserialize(Memory<byte> bytes)
  {
    // Need to create new byte array.
    // Otherwise, the data in memory would attach to the block caches.
    return bytes.ToArray();
  }

  public Memory<byte> Serialize(in Memory<byte> entry)
  {
    return entry;
  }

  public int GetMaxByteCount(in Memory<byte> entry) => entry.Length;

  public int Serialize(in Memory<byte> entry, Span<byte> destination)
  {
    entry.Span.CopyTo(destination);
    return entry.Length;
  }
}
