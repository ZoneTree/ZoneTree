using static ZoneTree.Composite.CompositeValueFormat;

namespace ZoneTree.Composite;

/// <summary>
/// Builds a composite value of any number of elements, one element at a time: the writing counterpart of
/// <see cref="CompositeValueReader"/>. <see cref="CompositeValue.Create{T1}(T1)"/> and its overloads build values
/// of up to eight elements in one call.
/// </summary>
/// <remarks>
/// Each element's type is the type argument of <see cref="Add{T}(T)"/>, as it is for
/// <see cref="CompositeValue.Create{T1}(T1)"/>, so an element passed as <see cref="object"/> is not supported. A
/// builder keeps its buffers across <see cref="Clear"/>, so one builder reused for many values allocates only the
/// array each <see cref="ToArray"/> returns. It is not safe for concurrent use.
/// </remarks>
/// <example>
/// <code>
/// var builder = new CompositeValueBuilder().Add(order.Customer);
/// foreach (decimal amount in order.Amounts)
///   builder.Add(amount);
/// var value = builder.ToArray();
/// </code>
/// </example>
public sealed class CompositeValueBuilder
{
  byte[] payloads = [];
  int payloadLength;
  byte[] tags = [];
  int[] ends = [];
  int count;

  /// <summary>A builder of a value with no elements.</summary>
  public CompositeValueBuilder()
  {
  }

  /// <summary>A builder of a value with no elements, with room for payloads of a number of bytes before it grows.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The capacity is negative.</exception>
  public CompositeValueBuilder(int capacity)
  {
    if (capacity < 0)
      throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A capacity cannot be negative.");
    payloads = capacity == 0 ? [] : new byte[capacity];
  }

  /// <summary>The number of elements added so far.</summary>
  public int Count => count;

  /// <summary>Appends an element.</summary>
  /// <returns>This builder.</returns>
  /// <exception cref="NotSupportedException">Composite values do not support the element's type, or it is descending.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  public CompositeValueBuilder Add<T>(T value)
  {
    // The element is measured first, so a value that cannot be an element leaves the builder as it was.
    var size = new CompositeValueWriter();
    size.Append(value);
    var length = size.PayloadLength;
    ReservePayload(length);
    var writer = CompositeValueWriter.PayloadsInto(payloads, payloadLength);
    writer.Append(value);
    Append((byte)writer.LastType, length);
    return this;
  }

  /// <summary>
  /// Appends elements of another composite value as they are, without decoding them, such as the fields a
  /// projection keeps. Each element is checked to be one of its type, as a read checks it, before any is
  /// appended.
  /// </summary>
  /// <param name="value">The composite value.</param>
  /// <param name="start">The index of the first element to append.</param>
  /// <param name="count">The number of elements to append.</param>
  /// <returns>This builder.</returns>
  /// <exception cref="ArgumentException">The bytes are not a composite value, or an element is malformed.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The value does not have the run of elements.</exception>
  public CompositeValueBuilder AddElements(ReadOnlySpan<byte> value, int start, int count)
  {
    var layout = CompositeValueLayout.Parse(value);
    if (start < 0 || start > layout.Count)
      throw new ArgumentOutOfRangeException(nameof(start), start, $"The value has {layout.Count} elements.");
    if (count < 0 || count > layout.Count - start)
      throw new ArgumentOutOfRangeException(nameof(count), count, $"The value has {layout.Count - start} elements from {start}.");
    for (var i = start; i < start + count; ++i)
    {
      if (!IsValidElement(layout.Tag(i), layout.Payload(i), checkText: false))
        throw Malformed(i);
    }
    for (var i = start; i < start + count; ++i)
    {
      var payload = layout.Payload(i);
      payload.CopyTo(ReservePayload(payload.Length));
      Append(layout.Tag(i), payload.Length);
    }
    return this;
  }

  /// <summary>The value built so far, in a new array.</summary>
  public byte[] ToArray()
  {
    var value = Allocate(count, payloadLength, out var width);
    tags.AsSpan(0, count).CopyTo(value.AsSpan(1 + width));
    var endsStart = 1 + width + count;
    for (var i = 0; i < count; ++i)
      WriteNumber(value.AsSpan(endsStart + i * width), width, ends[i]);
    payloads.AsSpan(0, payloadLength).CopyTo(value.AsSpan(HeaderLength(width, count)));
    return value;
  }

  /// <summary>Removes every element, keeping the buffers for the next value.</summary>
  /// <returns>This builder.</returns>
  public CompositeValueBuilder Clear()
  {
    payloadLength = 0;
    count = 0;
    return this;
  }

  /// <summary>The room after the payloads for a number of bytes, growing the buffer if it has less.</summary>
  Span<byte> ReservePayload(int length)
  {
    var required = payloadLength + length;
    if (required > payloads.Length)
    {
      var grown = new byte[Math.Max(required, Math.Max(payloads.Length * 2, 64))];
      payloads.AsSpan(0, payloadLength).CopyTo(grown);
      payloads = grown;
    }
    return payloads.AsSpan(payloadLength, length);
  }

  void Append(byte tag, int length)
  {
    if (count == tags.Length)
    {
      var capacity = Math.Max(tags.Length * 2, 8);
      Array.Resize(ref tags, capacity);
      Array.Resize(ref ends, capacity);
    }
    payloadLength += length;
    tags[count] = tag;
    ends[count] = payloadLength;
    ++count;
  }
}
