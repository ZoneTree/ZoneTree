using System.Runtime.InteropServices;

namespace ZoneTree.Serializers;

/// <summary>Serializes Guids as their 16 bytes, in the order <see cref="Guid.ToByteArray()"/> gives them.</summary>
public sealed class GuidSerializer : ISerializer<Guid>
{
  /// <exception cref="ArgumentOutOfRangeException">The bytes are fewer than a Guid's 16.</exception>
  // On a little-endian machine a Guid's memory is already in that order.
  public Guid Deserialize(Memory<byte> bytes)
    => BitConverter.IsLittleEndian
        ? MemoryMarshal.Read<Guid>(bytes.Span)
        : new Guid(bytes.Span.Slice(0, 16));

  public Memory<byte> Serialize(in Guid entry)
  {
    var bytes = new byte[16];
    Serialize(entry, bytes);
    return bytes;
  }

  public int GetMaxByteCount(in Guid entry) => 16;

  public int Serialize(in Guid entry, Span<byte> destination)
  {
    entry.TryWriteBytes(destination);
    return 16;
  }
}
