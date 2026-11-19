using ZoneTree.Serializers;

namespace ZoneTree.WAL;

/// <summary>
/// Serializes log records into a buffer of the calling thread, so writing a record does not allocate and the
/// record is written in one write. Each thread has its own buffer, so concurrent writers serialize in parallel.
/// </summary>
public static class WalEntrySerializer
{
  [ThreadStatic]
  static SerializationBuffer ThreadBuffer;

  /// <summary>
  /// Serializes the key and then the value after headerLength bytes for a record header, and returns the record:
  /// the header, the key, the value, and trailerLength bytes for a trailer such as a checksum. The caller fills in
  /// the header and the trailer. The record is valid until the thread serializes its next record, so a log writes
  /// it right away.
  /// </summary>
  public static Span<byte> SerializeRecord<TKey, TValue>(
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer,
      in TKey key,
      in TValue value,
      int headerLength,
      int trailerLength,
      out int keyLength,
      out int valueLength)
  {
    var buffer = (ThreadBuffer ??= new SerializationBuffer()).Get(checked(
        headerLength +
        keySerializer.GetMaxByteCount(in key) +
        valueSerializer.GetMaxByteCount(in value) +
        trailerLength));
    keyLength = keySerializer.Serialize(in key, buffer[headerLength..]);
    valueLength = valueSerializer.Serialize(in value, buffer[(headerLength + keyLength)..]);
    return buffer[..(headerLength + keyLength + valueLength + trailerLength)];
  }
}
