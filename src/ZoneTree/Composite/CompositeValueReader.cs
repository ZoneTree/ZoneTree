using static ZoneTree.Composite.CompositeValueFormat;

namespace ZoneTree.Composite;

/// <summary>
/// Walks the elements of a composite value in one pass, without allocating. <see cref="Next"/> moves to each
/// element in turn; the other members describe and read the element it is on.
/// </summary>
/// <example>
/// <code>
/// var reader = new CompositeValueReader(value);
/// while (reader.Next())
///   Console.WriteLine($"{reader.Index}: {reader.ElementType}, {reader.Payload.Length} bytes");
/// </code>
/// </example>
public ref struct CompositeValueReader
{
  readonly CompositeValueLayout layout;
  int index;

  /// <summary>A reader before the first element of a value.</summary>
  /// <exception cref="ArgumentException">The bytes are not a composite value.</exception>
  public CompositeValueReader(ReadOnlySpan<byte> value)
  {
    layout = CompositeValueLayout.Parse(value);
    index = -1;
  }

  /// <summary>The number of elements in the value.</summary>
  public readonly int Count => layout.Count;

  /// <summary>The index of the element the reader is on.</summary>
  public readonly int Index => index;

  /// <summary>Moves to the next element.</summary>
  /// <returns>False once the value has no more elements.</returns>
  public bool Next()
  {
    if (index < layout.Count)
      ++index;
    return index < layout.Count;
  }

  /// <summary>The element's type.</summary>
  /// <exception cref="InvalidOperationException">The reader is not on an element.</exception>
  /// <exception cref="ArgumentException">The element's type is not one composite values have.</exception>
  public readonly CompositeElementType ElementType => TypeOf(layout, Current());

  /// <summary>
  /// The element's payload, a slice of the value: a string's UTF-8 bytes, the bytes of a bytes element, and the
  /// little-endian bytes of a number.
  /// </summary>
  /// <exception cref="InvalidOperationException">The reader is not on an element.</exception>
  /// <exception cref="ArgumentException">The element's bounds are malformed.</exception>
  public readonly ReadOnlySpan<byte> Payload => layout.Payload(Current());

  /// <summary>Reads the element as a value of a type. A null element reads as null for strings, byte arrays and nullable value types.</summary>
  /// <exception cref="InvalidOperationException">The reader is not on an element.</exception>
  /// <exception cref="InvalidCastException">The element has another type.</exception>
  /// <exception cref="OverflowException">The type cannot hold the element's value exactly.</exception>
  /// <exception cref="ArgumentException">The element is malformed, or a string element's bytes are not valid UTF-8.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support the type.</exception>
  public readonly T Read<T>()
  {
    var current = Current();
    return ReadElement<T>(layout.Tag(current), layout.Payload(current), current);
  }

  /// <summary>Reads the element as a value of a type, as <see cref="Read{T}"/> does, unless the type does not hold it.</summary>
  /// <returns>
  /// False if the element is malformed or has another type, the type cannot hold its value exactly, or a string
  /// element's bytes are not valid UTF-8.
  /// </returns>
  /// <exception cref="InvalidOperationException">The reader is not on an element.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support the type.</exception>
  public readonly bool TryRead<T>(out T value) => TryReadElement(layout, Current(), out value);

  readonly int Current()
      => index < 0 || index >= layout.Count
          ? throw new InvalidOperationException("The reader is not on an element: call Next, and read while it returns true.")
          : index;

  internal static CompositeElementType TypeOf(CompositeValueLayout layout, int index)
  {
    var tag = layout.Tag(index);
    return tag <= (byte)CompositeElementType.Guid ? (CompositeElementType)tag : throw Malformed(index);
  }

  /// <summary>Reads an element, checked to be one of its type, by the reading rules every composite format shares.</summary>
  /// <exception cref="ArgumentException">The element is malformed.</exception>
  internal static T ReadElement<T>(byte tag, ReadOnlySpan<byte> payload, int index)
  {
    if (!IsValidElement(tag, payload, checkText: false))
      throw Malformed(index);
    return CompositeElementReading.Read<T, CompositeValueElementFormat>(payload, new ElementInfo((CompositeElementType)tag, tag, 0, index));
  }

  /// <summary>Reads an element by the rules of <see cref="ReadElement{T}"/>, false where it throws for the element.</summary>
  /// <exception cref="NotSupportedException">Composite values do not support the type.</exception>
  internal static bool TryReadElement<T>(CompositeValueLayout layout, int index, out T value)
  {
    CompositeElementReading.CheckReadable<T>();
    var tag = layout.Tag(index);
    if (!layout.TryPayload(index, out var payload) || !IsValidElement(tag, payload, checkText: false))
    {
      value = default;
      return false;
    }
    return CompositeElementReading.TryRead<T, CompositeValueElementFormat>(
        payload, new ElementInfo((CompositeElementType)tag, tag, 0, index), out value);
  }
}
