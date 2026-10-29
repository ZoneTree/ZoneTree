using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using ZoneTree.Serializers;

namespace ZoneTree.Transactional;

/// <summary>
/// Serializes a versioned value as its kind and its payload:
/// the user value, the system meta, or nothing for deletion markers.
/// </summary>
/// <remarks>
/// Disk segments store unmanaged types with their fixed size, so when the user value
/// is unmanaged every record is padded to the size of <see cref="VersionedValue{TValue}"/>,
/// and the user value bytes have the size of the user value.
/// </remarks>
/// <typeparam name="TValue">The user value type.</typeparam>
public sealed class VersionedValueSerializer<TValue> : ISerializer<VersionedValue<TValue>>
{
  static readonly bool IsFixedSize =
      !RuntimeHelpers.IsReferenceOrContainsReferences<VersionedValue<TValue>>();

  static readonly int FixedSize = IsFixedSize ? Unsafe.SizeOf<VersionedValue<TValue>>() : 0;

  /// <summary>
  /// The user value serializer.
  /// </summary>
  public ISerializer<TValue> ValueSerializer { get; }

  /// <summary>
  /// Creates the serializer.
  /// </summary>
  /// <param name="valueSerializer">The user value serializer.</param>
  public VersionedValueSerializer(ISerializer<TValue> valueSerializer)
  {
    ValueSerializer = valueSerializer;
  }

  /// <inheritdoc/>
  public VersionedValue<TValue> Deserialize(Memory<byte> bytes)
  {
    var kind = (VersionedValueKind)bytes.Span[0];
    switch (kind)
    {
      case VersionedValueKind.Value:
        var valueBytes = IsFixedSize ?
            bytes.Slice(1, Unsafe.SizeOf<TValue>()) :
            bytes.Slice(1);
        return VersionedValue<TValue>.FromValue(ValueSerializer.Deserialize(valueBytes));
      case VersionedValueKind.System:
        return VersionedValue<TValue>.FromSystem(
            BinaryPrimitives.ReadInt64LittleEndian(bytes.Span.Slice(1)));
      default:
        return new VersionedValue<TValue>(kind, 0, default);
    }
  }

  /// <inheritdoc/>
  public Memory<byte> Serialize(in VersionedValue<TValue> entry)
  {
    byte[] bytes;
    switch (entry.Kind)
    {
      case VersionedValueKind.Value:
        var valueBytes = ValueSerializer.Serialize(in entry.Value);
        bytes = new byte[Math.Max(FixedSize, 1 + valueBytes.Length)];
        valueBytes.Span.CopyTo(bytes.AsSpan(1));
        break;
      case VersionedValueKind.System:
        bytes = new byte[Math.Max(FixedSize, 1 + sizeof(long))];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1), entry.Meta);
        break;
      default:
        bytes = new byte[Math.Max(FixedSize, 1)];
        break;
    }
    bytes[0] = (byte)entry.Kind;
    return bytes;
  }
}
