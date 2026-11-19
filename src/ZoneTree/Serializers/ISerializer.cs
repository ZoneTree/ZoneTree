namespace ZoneTree.Serializers;

/// <summary>
/// Generic Serializer interface for any type.
/// </summary>
/// <typeparam name="TEntry"></typeparam>
/// <remarks>
/// <see cref="Serialize(in TEntry, Span{byte})"/> must write the bytes <see cref="Serialize(in TEntry)"/> returns
/// for the same entry, because both forms write the same files.
/// </remarks>
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

  /// <summary>
  /// Returns at least the number of bytes that <see cref="Serialize(in TEntry, Span{byte})"/> writes for the entry.
  /// </summary>
  /// <param name="entry">The entry</param>
  int GetMaxByteCount(in TEntry entry);

  /// <summary>
  /// Writes the entry's bytes at the start of the destination instead of a new array, and returns how many it
  /// wrote. Disk segments and write-ahead logs write every key and value this way, into buffers they reuse, so the
  /// destination can hold earlier bytes, and every byte the count covers must be written.
  /// </summary>
  /// <param name="entry">The entry</param>
  /// <param name="destination">A buffer of at least <see cref="GetMaxByteCount"/> bytes.</param>
  /// <returns>The number of bytes written.</returns>
  int Serialize(in TEntry entry, Span<byte> destination);
}
