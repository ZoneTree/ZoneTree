using static ZoneTree.Composite.CompositeKeyFormat;

namespace ZoneTree.Composite;

/// <summary>
/// Walks the elements of a composite key in one pass, without allocating. <see cref="Next"/> moves to each
/// element in turn; the other members describe and read the element it is on.
/// </summary>
/// <example>
/// <code>
/// var reader = new CompositeKeyReader(key);
/// while (reader.Next())
///   Console.WriteLine($"{reader.Index}: {reader.ElementType}, {reader.ElementBytes.Length} bytes");
/// </code>
/// </example>
public ref struct CompositeKeyReader
{
  readonly ReadOnlySpan<byte> key;
  int offset;
  int length;
  int index;

  /// <summary>A reader before the first element of a key.</summary>
  public CompositeKeyReader(ReadOnlySpan<byte> key)
  {
    this.key = key;
    index = -1;
  }

  /// <summary>Moves to the next element.</summary>
  /// <returns>False once the key has no more elements.</returns>
  /// <exception cref="ArgumentException">The bytes of the next element are not a composite key element.</exception>
  public bool Next()
  {
    if (TryNext())
      return true;
    if (AtEnd)
      return false;
    var at = offset;
    offset = key.Length;
    throw Malformed(at);
  }

  /// <summary>
  /// Moves to the next element, without throwing: false at the end of the key, and at bytes that are not an
  /// element, where <see cref="Offset"/> stays and <see cref="AtEnd"/> is false.
  /// </summary>
  internal bool TryNext()
  {
    offset += length;
    length = 0;
    if (offset >= key.Length)
    {
      offset = key.Length;
      return false;
    }
    var measured = Measure(key, offset);
    if (measured < 0)
      return false;
    length = measured;
    ++index;
    return true;
  }

  /// <summary>Whether the reader passed the key's last element.</summary>
  internal readonly bool AtEnd => offset >= key.Length;

  /// <summary>The index of the element the reader is on.</summary>
  public readonly int Index => index;

  /// <summary>Where the element starts in the key.</summary>
  public readonly int Offset => offset;

  /// <summary>The element's encoded bytes, a slice of the key that can be appended to another key as they are.</summary>
  /// <exception cref="InvalidOperationException">The reader is not on an element.</exception>
  public readonly ReadOnlySpan<byte> ElementBytes => Current();

  /// <summary>The element's type.</summary>
  /// <exception cref="InvalidOperationException">The reader is not on an element.</exception>
  public readonly CompositeElementType ElementType => TypeOf(AscendingTag(Current()[0], out _));

  /// <summary>Whether the element sorts in descending order.</summary>
  /// <exception cref="InvalidOperationException">The reader is not on an element.</exception>
  public readonly bool IsDescending => Current()[0] >= 0x80;

  /// <summary>
  /// Reads the element as a value of a type, ascending or descending alike. A null element reads as null for
  /// strings, byte arrays and nullable value types.
  /// </summary>
  /// <exception cref="InvalidOperationException">The reader is not on an element.</exception>
  /// <exception cref="InvalidCastException">The element has another type.</exception>
  /// <exception cref="OverflowException">The type cannot hold the element's value exactly.</exception>
  /// <exception cref="ArgumentException">A string element's bytes are not valid UTF-8.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support the type.</exception>
  public readonly T Read<T>()
  {
    var element = Current();
    var tag = AscendingTag(element[0], out var mask);
    return CompositeElementReading.Read<T, CompositeKeyElementFormat>(element.Slice(1), new ElementInfo(TypeOf(tag), tag, mask, index));
  }

  /// <summary>Reads the element as a value of a type, as <see cref="Read{T}"/> does, unless the type does not hold it.</summary>
  /// <returns>
  /// False if the element has another type, the type cannot hold its value exactly, or a string element's bytes
  /// are not valid UTF-8.
  /// </returns>
  /// <exception cref="InvalidOperationException">The reader is not on an element.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support the type.</exception>
  public readonly bool TryRead<T>(out T value)
  {
    var element = Current();
    var tag = AscendingTag(element[0], out var mask);
    return CompositeElementReading.TryRead<T, CompositeKeyElementFormat>(
        element.Slice(1), new ElementInfo(TypeOf(tag), tag, mask, index), out value);
  }

  readonly ReadOnlySpan<byte> Current()
      => length == 0
          ? throw new InvalidOperationException("The reader is not on an element: call Next, and read while it returns true.")
          : key.Slice(offset, length);
}
