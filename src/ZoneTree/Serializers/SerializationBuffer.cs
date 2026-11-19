namespace ZoneTree.Serializers;

/// <summary>
/// A buffer that a writer serializes its entries into, one after another, instead of a new array each. It grows to
/// the longest entry up to its kept length. A longer entry gets an array of its own, so the buffer does not hold
/// that size. A buffer serves one writer at a time.
/// </summary>
public sealed class SerializationBuffer
{
  /// <summary>
  /// The default kept length, for a buffer that lives long, such as one for each thread.
  /// </summary>
  public const int DefaultMaxKeptLength = 64 * 1024;

  const int MinLength = 256;

  readonly int MaxKeptLength;

  byte[] Buffer;

  /// <summary>
  /// Creates a buffer that keeps up to the length for the next entries.
  /// </summary>
  /// <param name="maxKeptLength">
  /// The longest buffer that is kept. A buffer that lives as long as one task, such as writing one disk segment,
  /// can keep any length, so a task of long entries reuses one buffer.
  /// </param>
  public SerializationBuffer(int maxKeptLength = DefaultMaxKeptLength)
  {
    MaxKeptLength = maxKeptLength;
  }

  /// <summary>
  /// Returns a buffer of at least the length. Its bytes are valid until the next call, and can hold bytes of
  /// earlier entries.
  /// </summary>
  public Span<byte> Get(int length)
  {
    var buffer = Buffer;
    if (buffer != null && buffer.Length >= length)
      return buffer;
    if (length > MaxKeptLength)
      return new byte[length];
    // A short buffer grows by doubling, so a writer rarely reallocates it. A longer one takes the length asked
    // for, so it is never much longer than the longest entry.
    var grown = Math.Min(Math.Max((buffer?.Length ?? 0) * 2, MinLength), DefaultMaxKeptLength);
    buffer = new byte[Math.Max(length, grown)];
    Buffer = buffer;
    return buffer;
  }
}
