using System.Runtime.InteropServices;

namespace ZoneTree.Serializers;

/// <summary>Serializes read-only byte memory as its bytes.</summary>
public sealed class ReadOnlyByteMemorySerializer : ISerializer<ReadOnlyMemory<byte>>
{
  public ReadOnlyMemory<byte> Deserialize(Memory<byte> bytes)
  {
    // A copy, so the memory does not hold on to a block cache.
    return bytes.ToArray();
  }

  public Memory<byte> Serialize(in ReadOnlyMemory<byte> entry)
    => MemoryMarshal.AsMemory(entry);
}
