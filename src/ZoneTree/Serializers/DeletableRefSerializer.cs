using ZoneTree.PresetTypes;

namespace ZoneTree.Serializers;

public sealed class DeletableRefSerializer<TValue> : ISerializer<Deletable<TValue>>
{
  readonly ISerializer<TValue> ValueSerializer;

  public DeletableRefSerializer(ISerializer<TValue> valueSerializer)
  {
    ValueSerializer = valueSerializer;
  }

  public Deletable<TValue> Deserialize(Memory<byte> bytes)
  {
    var isDeletedOffset = bytes.Length - 1;
    var b1 = bytes.Slice(0, isDeletedOffset);
    var isDeleted = bytes.Span[isDeletedOffset] != 0;
    return new Deletable<TValue>(
        ValueSerializer.Deserialize(b1),
        isDeleted);
  }

  public Memory<byte> Serialize(in Deletable<TValue> entry)
  {
    var b1 = ValueSerializer.Serialize(entry.Value);
    var len = b1.Length;
    var b2 = new byte[len + 1];
    b1.CopyTo(b2);
    b2[len] = entry.IsDeleted ? (byte)1 : (byte)0;
    return b2;
  }

  public int GetMaxByteCount(in Deletable<TValue> entry)
      => ValueSerializer.GetMaxByteCount(entry.Value) + 1;

  public int Serialize(in Deletable<TValue> entry, Span<byte> destination)
  {
    var len = ValueSerializer.Serialize(entry.Value, destination);
    destination[len] = entry.IsDeleted ? (byte)1 : (byte)0;
    return len + 1;
  }
}
