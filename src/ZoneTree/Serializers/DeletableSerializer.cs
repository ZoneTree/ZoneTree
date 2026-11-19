using System.Runtime.CompilerServices;
using ZoneTree.PresetTypes;

namespace ZoneTree.Serializers;

public sealed class DeletableSerializer<TValue> : ISerializer<Deletable<TValue>>
    where TValue : unmanaged
{
  public Deletable<TValue> Deserialize(Memory<byte> bytes)
  {
    return BinarySerializerHelper.FromByteArray<Deletable<TValue>>(bytes);
  }

  public Memory<byte> Serialize(in Deletable<TValue> entry)
  {
    return BinarySerializerHelper.ToByteArray(entry);
  }

  public int GetMaxByteCount(in Deletable<TValue> entry) => Unsafe.SizeOf<Deletable<TValue>>();

  public int Serialize(in Deletable<TValue> entry, Span<byte> destination)
      => BinarySerializerHelper.Write(entry, destination);
}
