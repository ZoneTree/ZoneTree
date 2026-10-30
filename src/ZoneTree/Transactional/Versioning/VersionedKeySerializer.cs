using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using ZoneTree.Serializers;

namespace ZoneTree.Transactional.Versioning;

/// <summary>
/// Serializes a versioned key as its kind, its version and,
/// for data records, the user key.
/// </summary>
/// <remarks>
/// Disk segments store unmanaged types with their fixed size, so when the user key
/// is unmanaged every record is padded to the size of <see cref="VersionedKey{TKey}"/>,
/// and the user key bytes have the size of the user key.
/// </remarks>
/// <typeparam name="TKey">The user key type.</typeparam>
public sealed class VersionedKeySerializer<TKey> : ISerializer<VersionedKey<TKey>>
{
  const int HeaderSize = sizeof(byte) + sizeof(long);

  static readonly bool IsFixedSize =
      !RuntimeHelpers.IsReferenceOrContainsReferences<VersionedKey<TKey>>();

  static readonly int FixedSize = IsFixedSize ? Unsafe.SizeOf<VersionedKey<TKey>>() : 0;

  /// <summary>
  /// The user key serializer.
  /// </summary>
  public ISerializer<TKey> KeySerializer { get; }

  /// <summary>
  /// Creates the serializer.
  /// </summary>
  /// <param name="keySerializer">The user key serializer.</param>
  public VersionedKeySerializer(ISerializer<TKey> keySerializer)
  {
    KeySerializer = keySerializer;
  }

  /// <inheritdoc/>
  public VersionedKey<TKey> Deserialize(Memory<byte> bytes)
  {
    var span = bytes.Span;
    var kind = (VersionedKeyKind)span[0];
    var version = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(1));
    TKey key = default;
    if (kind == VersionedKeyKind.Data)
    {
      var keyBytes = IsFixedSize ?
          bytes.Slice(HeaderSize, Unsafe.SizeOf<TKey>()) :
          bytes.Slice(HeaderSize);
      key = KeySerializer.Deserialize(keyBytes);
    }
    return new VersionedKey<TKey>(kind, in key, version);
  }

  /// <inheritdoc/>
  public Memory<byte> Serialize(in VersionedKey<TKey> entry)
  {
    var keyBytes = entry.Kind == VersionedKeyKind.Data ?
        KeySerializer.Serialize(in entry.Key) :
        Memory<byte>.Empty;
    var bytes = new byte[Math.Max(FixedSize, HeaderSize + keyBytes.Length)];
    bytes[0] = (byte)entry.Kind;
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1), entry.Version);
    keyBytes.Span.CopyTo(bytes.AsSpan(HeaderSize));
    return bytes;
  }
}
