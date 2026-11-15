using static ZoneTree.Composite.CompositeKeyFormat;

namespace ZoneTree.Composite;

/// <summary>
/// Builds a composite key of any number of elements, one element at a time: the writing counterpart of
/// <see cref="CompositeKeyReader"/>. <see cref="CompositeKey.Create{T1}(T1)"/> and its overloads build keys of up
/// to eight elements in one call.
/// </summary>
/// <remarks>
/// Each element's type is the type argument of <see cref="Add{T}(T)"/>, as it is for
/// <see cref="CompositeKey.Create{T1}(T1)"/>, so an element passed as <see cref="object"/> is not supported. A
/// builder keeps its buffer across <see cref="Clear"/>, so one builder reused for many keys allocates only the
/// array each <see cref="ToArray"/> returns. It is not safe for concurrent use.
/// </remarks>
/// <example>
/// <code>
/// var builder = new CompositeKeyBuilder().Add("by-tags");
/// foreach (string tag in tags)
///   builder.Add(tag);
/// var key = builder.Add(CompositeKey.Descending(placed)).Add(id).ToArray();
/// </code>
/// </example>
public sealed class CompositeKeyBuilder
{
  byte[] buffer;
  int length;

  /// <summary>A builder of an empty key.</summary>
  public CompositeKeyBuilder()
  {
    buffer = [];
  }

  /// <summary>A builder of an empty key, with room for a key of a number of bytes before it grows.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The capacity is negative.</exception>
  public CompositeKeyBuilder(int capacity)
  {
    if (capacity < 0)
      throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A capacity cannot be negative.");
    buffer = capacity == 0 ? [] : new byte[capacity];
  }

  /// <summary>The length of the key built so far, in bytes.</summary>
  public int Length => length;

  /// <summary>The key built so far, valid until the builder changes.</summary>
  public ReadOnlySpan<byte> AsSpan() => buffer.AsSpan(0, length);

  /// <summary>Appends an element, which a <see cref="Descending{T}"/> makes sort in descending order.</summary>
  /// <returns>This builder.</returns>
  /// <exception cref="NotSupportedException">Composite keys do not support the element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A BigInteger element is wider than 255 bytes.</exception>
  public CompositeKeyBuilder Add<T>(T value)
  {
    // The element is measured first, so a value that cannot be an element leaves the key as it was.
    var size = new CompositeKeyWriter();
    size.Append(value);
    Reserve(size.Length);
    var writer = new CompositeKeyWriter(buffer, length);
    writer.Append(value);
    length += size.Length;
    return this;
  }

  /// <summary>
  /// Appends encoded elements as they are, such as the elements <see cref="CompositeKey.ElementBytes(ReadOnlySpan{byte}, int, int)"/>
  /// returns, or a whole key.
  /// </summary>
  /// <returns>This builder.</returns>
  /// <exception cref="ArgumentException">The bytes are not whole composite key elements.</exception>
  public CompositeKeyBuilder AddElements(ReadOnlySpan<byte> elements)
  {
    for (var offset = 0; offset < elements.Length;)
    {
      var measured = Measure(elements, offset);
      if (measured < 0)
        throw Malformed(offset);
      offset += measured;
    }
    elements.CopyTo(Reserve(elements.Length));
    length += elements.Length;
    return this;
  }

  /// <summary>The key built so far, in a new array.</summary>
  public byte[] ToArray() => AsSpan().ToArray();

  /// <summary>Empties the key, keeping the buffer for the next one.</summary>
  /// <returns>This builder.</returns>
  public CompositeKeyBuilder Clear()
  {
    length = 0;
    return this;
  }

  /// <summary>The room after the key for a number of bytes, growing the buffer if it has less.</summary>
  Span<byte> Reserve(int count)
  {
    var required = length + count;
    if (required > buffer.Length)
    {
      var grown = new byte[Math.Max(required, Math.Max(buffer.Length * 2, 32))];
      buffer.AsSpan(0, length).CopyTo(grown);
      buffer = grown;
    }
    return buffer.AsSpan(length, count);
  }
}
