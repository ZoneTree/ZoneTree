using System;
using System.Runtime.CompilerServices;

namespace ZoneTree.Serializers;

public sealed class StructSerializer<TType> : ISerializer<TType> where TType : unmanaged
{
  public TType Deserialize(Memory<byte> bytes)
  {
    return BinarySerializerHelper.FromByteArray<TType>(bytes);
  }

  public Memory<byte> Serialize(in TType entry)
  {
    return BinarySerializerHelper.ToByteArray(entry);
  }

  public int GetMaxByteCount(in TType entry) => Unsafe.SizeOf<TType>();

  public int Serialize(in TType entry, Span<byte> destination)
      => BinarySerializerHelper.Write(entry, destination);
}
