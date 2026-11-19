namespace ZoneTree.Serializers;

/// <summary>
/// Generic Serializer interface for any type.
/// </summary>
/// <typeparam name="TEntry"></typeparam>
public interface ISerializer<TEntry>
{
  /// <summary>
  /// Deserialize the bytes into entry type.
  /// </summary>
  /// <param name="bytes">
  /// The bytes to be deserialized. They are usually part of a disk segment's decompressed block, so the entry must
  /// copy any bytes it keeps. An entry that kept a slice would keep the whole block in memory.
  /// </param>
  /// <returns>The deserialized entry.</returns>
  TEntry Deserialize(Memory<byte> bytes);

  /// <summary>
  /// Serialize the entry into byte array.
  /// </summary>
  /// <param name="entry">The entry</param>
  /// <returns>The serialized bytes.</returns>
  Memory<byte> Serialize(in TEntry entry);
}
