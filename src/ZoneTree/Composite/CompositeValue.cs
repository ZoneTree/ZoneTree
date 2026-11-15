using static ZoneTree.Composite.CompositeValueFormat;

namespace ZoneTree.Composite;

/// <summary>
/// Encodes several values into one byte-array value and reads them back, each from its position without reading
/// the others: the value counterpart of <see cref="CompositeKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// A composite value supports the element types of a composite key, but has no order, so its elements cannot be
/// descending. Its format is built for size and speed instead: a header gives each element's type and where it
/// ends, strings are stored as UTF-8 and bytes as they are, with nothing escaped, and numbers little-endian in
/// their own sizes. Every element reads back as it was written: a decimal keeps its scale, a DateTime its kind, a
/// DateTimeOffset its offset, and a float stays 4 bytes.
/// </para>
/// <para>
/// Each element records its type, so a value is read without knowing what wrote it. Every integer type,
/// BigInteger, char and enum are integers, read as any integer type that holds the value, and Half, float and
/// double are floating values, read as any of them that holds the value exactly. A value grows by elements added
/// at its end: <see cref="Decode{T1}(ReadOnlySpan{byte})"/> and its overloads read the leading elements, and
/// <see cref="ElementCount"/> tells whether a later one is there.
/// </para>
/// <para>
/// A composite value is never empty, so it is never the empty byte array a tree takes for a deletion.
/// <see cref="Create{T1}(T1)"/> and its overloads build values of up to eight elements in one call, and
/// <see cref="CompositeValueBuilder"/> builds values of any number.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var value = CompositeValue.Create("bob", 42L, 19.99m);
/// var total = CompositeValue.Get&lt;decimal&gt;(value, 2);
/// var (name, id) = CompositeValue.Decode&lt;string, long&gt;(value);
/// </code>
/// </example>
public static class CompositeValue
{
  /// <summary>Encodes a value of one element.</summary>
  /// <exception cref="NotSupportedException">Composite values do not support the element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  public static byte[] Create<T1>(T1 first)
  {
    var size = new CompositeValueWriter();
    size.Append(first);
    var value = Allocate(size.Count, size.PayloadLength, out var width);
    var writer = new CompositeValueWriter(value, width, size.Count);
    writer.Append(first);
    return value;
  }

  /// <summary>Encodes a value of 2 elements.</summary>
  /// <exception cref="NotSupportedException">Composite values do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  public static byte[] Create<T1, T2>(T1 first, T2 second)
  {
    var size = new CompositeValueWriter();
    size.Append(first);
    size.Append(second);
    var value = Allocate(size.Count, size.PayloadLength, out var width);
    var writer = new CompositeValueWriter(value, width, size.Count);
    writer.Append(first);
    writer.Append(second);
    return value;
  }

  /// <summary>Encodes a value of 3 elements.</summary>
  /// <exception cref="NotSupportedException">Composite values do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  public static byte[] Create<T1, T2, T3>(T1 first, T2 second, T3 third)
  {
    var size = new CompositeValueWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    var value = Allocate(size.Count, size.PayloadLength, out var width);
    var writer = new CompositeValueWriter(value, width, size.Count);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    return value;
  }

  /// <summary>Encodes a value of 4 elements.</summary>
  /// <exception cref="NotSupportedException">Composite values do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  public static byte[] Create<T1, T2, T3, T4>(T1 first, T2 second, T3 third, T4 fourth)
  {
    var size = new CompositeValueWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    size.Append(fourth);
    var value = Allocate(size.Count, size.PayloadLength, out var width);
    var writer = new CompositeValueWriter(value, width, size.Count);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    writer.Append(fourth);
    return value;
  }

  /// <summary>Encodes a value of 5 elements.</summary>
  /// <exception cref="NotSupportedException">Composite values do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  public static byte[] Create<T1, T2, T3, T4, T5>(T1 first, T2 second, T3 third, T4 fourth, T5 fifth)
  {
    var size = new CompositeValueWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    size.Append(fourth);
    size.Append(fifth);
    var value = Allocate(size.Count, size.PayloadLength, out var width);
    var writer = new CompositeValueWriter(value, width, size.Count);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    writer.Append(fourth);
    writer.Append(fifth);
    return value;
  }

  /// <summary>Encodes a value of 6 elements.</summary>
  /// <exception cref="NotSupportedException">Composite values do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  public static byte[] Create<T1, T2, T3, T4, T5, T6>(T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth)
  {
    var size = new CompositeValueWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    size.Append(fourth);
    size.Append(fifth);
    size.Append(sixth);
    var value = Allocate(size.Count, size.PayloadLength, out var width);
    var writer = new CompositeValueWriter(value, width, size.Count);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    writer.Append(fourth);
    writer.Append(fifth);
    writer.Append(sixth);
    return value;
  }

  /// <summary>Encodes a value of 7 elements.</summary>
  /// <exception cref="NotSupportedException">Composite values do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  public static byte[] Create<T1, T2, T3, T4, T5, T6, T7>(
      T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth, T7 seventh)
  {
    var size = new CompositeValueWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    size.Append(fourth);
    size.Append(fifth);
    size.Append(sixth);
    size.Append(seventh);
    var value = Allocate(size.Count, size.PayloadLength, out var width);
    var writer = new CompositeValueWriter(value, width, size.Count);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    writer.Append(fourth);
    writer.Append(fifth);
    writer.Append(sixth);
    writer.Append(seventh);
    return value;
  }

  /// <summary>Encodes a value of 8 elements. <see cref="CompositeValueBuilder"/> builds longer values.</summary>
  /// <exception cref="NotSupportedException">Composite values do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  public static byte[] Create<T1, T2, T3, T4, T5, T6, T7, T8>(
      T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth, T7 seventh, T8 eighth)
  {
    var size = new CompositeValueWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    size.Append(fourth);
    size.Append(fifth);
    size.Append(sixth);
    size.Append(seventh);
    size.Append(eighth);
    var value = Allocate(size.Count, size.PayloadLength, out var width);
    var writer = new CompositeValueWriter(value, width, size.Count);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    writer.Append(fourth);
    writer.Append(fifth);
    writer.Append(sixth);
    writer.Append(seventh);
    writer.Append(eighth);
    return value;
  }

  /// <summary>Reads a value's first element; any elements after it are left unread.</summary>
  /// <exception cref="ArgumentException">The value has no elements, or its bytes are not a composite value.</exception>
  /// <exception cref="InvalidCastException">The element has another type.</exception>
  /// <exception cref="OverflowException">The type cannot hold the element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support the type.</exception>
  public static T1 Decode<T1>(ReadOnlySpan<byte> value)
  {
    var layout = Leading(value, 1);
    return Read<T1>(layout, 0);
  }

  /// <summary>Reads a value's first 2 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The value has fewer elements, or its bytes are not a composite value.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support a type.</exception>
  public static (T1, T2) Decode<T1, T2>(ReadOnlySpan<byte> value)
  {
    var layout = Leading(value, 2);
    return (Read<T1>(layout, 0), Read<T2>(layout, 1));
  }

  /// <summary>Reads a value's first 3 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The value has fewer elements, or its bytes are not a composite value.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support a type.</exception>
  public static (T1, T2, T3) Decode<T1, T2, T3>(ReadOnlySpan<byte> value)
  {
    var layout = Leading(value, 3);
    return (Read<T1>(layout, 0), Read<T2>(layout, 1), Read<T3>(layout, 2));
  }

  /// <summary>Reads a value's first 4 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The value has fewer elements, or its bytes are not a composite value.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support a type.</exception>
  public static (T1, T2, T3, T4) Decode<T1, T2, T3, T4>(ReadOnlySpan<byte> value)
  {
    var layout = Leading(value, 4);
    return (Read<T1>(layout, 0), Read<T2>(layout, 1), Read<T3>(layout, 2), Read<T4>(layout, 3));
  }

  /// <summary>Reads a value's first 5 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The value has fewer elements, or its bytes are not a composite value.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support a type.</exception>
  public static (T1, T2, T3, T4, T5) Decode<T1, T2, T3, T4, T5>(ReadOnlySpan<byte> value)
  {
    var layout = Leading(value, 5);
    return (Read<T1>(layout, 0), Read<T2>(layout, 1), Read<T3>(layout, 2), Read<T4>(layout, 3), Read<T5>(layout, 4));
  }

  /// <summary>Reads a value's first 6 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The value has fewer elements, or its bytes are not a composite value.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support a type.</exception>
  public static (T1, T2, T3, T4, T5, T6) Decode<T1, T2, T3, T4, T5, T6>(ReadOnlySpan<byte> value)
  {
    var layout = Leading(value, 6);
    return (Read<T1>(layout, 0), Read<T2>(layout, 1), Read<T3>(layout, 2), Read<T4>(layout, 3), Read<T5>(layout, 4),
        Read<T6>(layout, 5));
  }

  /// <summary>Reads a value's first 7 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The value has fewer elements, or its bytes are not a composite value.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support a type.</exception>
  public static (T1, T2, T3, T4, T5, T6, T7) Decode<T1, T2, T3, T4, T5, T6, T7>(ReadOnlySpan<byte> value)
  {
    var layout = Leading(value, 7);
    return (Read<T1>(layout, 0), Read<T2>(layout, 1), Read<T3>(layout, 2), Read<T4>(layout, 3), Read<T5>(layout, 4),
        Read<T6>(layout, 5), Read<T7>(layout, 6));
  }

  /// <summary>Reads a value's first 8 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The value has fewer elements, or its bytes are not a composite value.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support a type.</exception>
  public static (T1, T2, T3, T4, T5, T6, T7, T8) Decode<T1, T2, T3, T4, T5, T6, T7, T8>(ReadOnlySpan<byte> value)
  {
    var layout = Leading(value, 8);
    return (Read<T1>(layout, 0), Read<T2>(layout, 1), Read<T3>(layout, 2), Read<T4>(layout, 3), Read<T5>(layout, 4),
        Read<T6>(layout, 5), Read<T7>(layout, 6), Read<T8>(layout, 7));
  }

  static CompositeValueLayout Leading(ReadOnlySpan<byte> value, int expected)
  {
    var layout = CompositeValueLayout.Parse(value);
    if (layout.Count < expected)
      throw new ArgumentException($"The value has {layout.Count} elements, fewer than {expected}.", nameof(value));
    return layout;
  }

  static T Read<T>(CompositeValueLayout layout, int index)
      => CompositeValueReader.ReadElement<T>(layout.Tag(index), layout.Payload(index), index);

  /// <summary>Reads one element of a value, without reading the others.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The value has no element at the index.</exception>
  /// <exception cref="ArgumentException">The value's bytes are not a composite value.</exception>
  /// <exception cref="InvalidCastException">The element has another type.</exception>
  /// <exception cref="OverflowException">The type cannot hold the element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support the type.</exception>
  public static T Get<T>(ReadOnlySpan<byte> value, int index)
  {
    var layout = CompositeValueLayout.Parse(value);
    layout.CheckIndex(index);
    return Read<T>(layout, index);
  }

  /// <summary>The number of elements in a value, from its header.</summary>
  /// <exception cref="ArgumentException">The value's bytes are not a composite value.</exception>
  public static int ElementCount(ReadOnlySpan<byte> value) => CompositeValueLayout.Parse(value).Count;

  /// <summary>The type of one element of a value, from its header.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The value has no element at the index.</exception>
  /// <exception cref="ArgumentException">The value's bytes are not a composite value.</exception>
  public static CompositeElementType ElementType(ReadOnlySpan<byte> value, int index)
  {
    var layout = CompositeValueLayout.Parse(value);
    layout.CheckIndex(index);
    return CompositeValueReader.TypeOf(layout, index);
  }

  /// <summary>
  /// The payload of one element of a value, a slice of the value: a string's UTF-8 bytes, the bytes of a bytes
  /// element, and the little-endian bytes of a number.
  /// </summary>
  /// <exception cref="ArgumentOutOfRangeException">The value has no element at the index.</exception>
  /// <exception cref="ArgumentException">The value's bytes are not a composite value.</exception>
  public static ReadOnlySpan<byte> ElementPayload(ReadOnlySpan<byte> value, int index)
  {
    var layout = CompositeValueLayout.Parse(value);
    layout.CheckIndex(index);
    return layout.Payload(index);
  }

  /// <summary>
  /// A copy of a value with one element replaced, such as one field of a row: the other elements are copied as
  /// they are, without decoding them, into one new array.
  /// </summary>
  /// <exception cref="ArgumentOutOfRangeException">The value has no element at the index.</exception>
  /// <exception cref="ArgumentException">The value's bytes are not a composite value, an element is malformed, or a string element is not valid UTF-16.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support the element's type, or it is descending.</exception>
  public static byte[] With<T>(ReadOnlySpan<byte> value, int index, T element)
  {
    var layout = CompositeValueLayout.Parse(value);
    layout.CheckIndex(index);
    return Rebuild(layout, index, replaces: true, element);
  }

  /// <summary>
  /// A copy of a value with an element added at its end, such as a field a newer row has: the other elements are
  /// copied as they are, without decoding them, into one new array.
  /// </summary>
  /// <exception cref="ArgumentException">The value's bytes are not a composite value, an element is malformed, or a string element is not valid UTF-16.</exception>
  /// <exception cref="NotSupportedException">Composite values do not support the element's type, or it is descending.</exception>
  public static byte[] Append<T>(ReadOnlySpan<byte> value, T element)
  {
    var layout = CompositeValueLayout.Parse(value);
    return Rebuild(layout, layout.Count, replaces: false, element);
  }

  /// <summary>
  /// Writes a value of the layout's elements, with an element at an index that replaces the one there or comes
  /// after the last. The others are checked, as a read checks them, and copied as they are; a replaced element's
  /// bounds are checked too.
  /// </summary>
  static byte[] Rebuild<T>(CompositeValueLayout layout, int index, bool replaces, in T element)
  {
    for (var i = 0; i < layout.Count; ++i)
    {
      if ((!replaces || i != index) && !IsValidElement(layout.Tag(i), layout.Payload(i), checkText: false))
        throw Malformed(i);
    }
    var before = index == 0 ? 0 : layout.End(index - 1);
    var after = replaces ? layout.End(index) : layout.PayloadLength;
    // The elements around a replaced one bound it from outside, so its bounds can still run backwards.
    if (before > after)
      throw Malformed(index);
    var size = new CompositeValueWriter();
    size.Append(element);
    var length = size.PayloadLength;
    var count = replaces ? layout.Count : layout.Count + 1;
    var change = length - (after - before);
    var result = Allocate(count, layout.PayloadLength + change, out var width);
    var header = HeaderLength(width, count);

    var payloads = layout.Payloads;
    payloads.Slice(0, before).CopyTo(result.AsSpan(header));
    var writer = CompositeValueWriter.PayloadsInto(result, header + before);
    writer.Append(element);
    payloads.Slice(after).CopyTo(result.AsSpan(header + before + length));

    var tags = result.AsSpan(1 + width, count);
    layout.Tags.CopyTo(tags);
    tags[index] = (byte)writer.LastType;
    var ends = result.AsSpan(1 + width + count);
    for (var i = 0; i < count; ++i)
    {
      var end = i < index ? layout.End(i) : i == index ? before + length : layout.End(i) + change;
      WriteNumber(ends.Slice(i * width), width, end);
    }
    return result;
  }

  /// <summary>
  /// Whether bytes are a composite value: a header that fits them, elements that end in order, and payloads of
  /// their types, with strings of valid UTF-8.
  /// </summary>
  public static bool IsValid(ReadOnlySpan<byte> value)
  {
    if (!CompositeValueLayout.TryParse(value, out var layout))
      return false;
    for (var i = 0; i < layout.Count; ++i)
    {
      if (!layout.TryPayload(i, out var payload) || !IsValidElement(layout.Tag(i), payload, checkText: true))
        return false;
    }
    return true;
  }
}
