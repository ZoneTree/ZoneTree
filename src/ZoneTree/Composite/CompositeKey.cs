using static ZoneTree.Composite.CompositeKeyFormat;

namespace ZoneTree.Composite;

/// <summary>
/// Encodes several values into one byte-array key whose bytes sort as the values do, element by element, and
/// reads them back. The keys sort with the built-in lexicographic comparer of byte-array keys.
/// </summary>
/// <remarks>
/// <para>
/// A key is its elements one after another, with no header, so two keys joined end to end are the key of their
/// elements together, and a key's bytes start every key that extends it. One tree can hold rows and the entries
/// of several indexes, each under a leading element of its own, and <see cref="Range"/> bounds the keys
/// that start with given elements.
/// </para>
/// <para>
/// Elements can be any .NET integer type, BigInteger, char, an enum, Half, float, double, decimal, bool,
/// DateTime, DateTimeOffset, DateOnly, TimeOnly, TimeSpan, Guid, string, a byte array or byte memory, their
/// nullable types, and <see cref="Descending{T}"/> of any of them. Each element records its type, so a key is
/// read without knowing what wrote it: every integer type, BigInteger, char and enum are integers, compared by
/// value, and Half, float and double are floating values. Elements of different types sort by type, in the
/// order of <see cref="CompositeElementType"/>.
/// </para>
/// <para>
/// Every value has one encoding: 0.0 and -0.0, every NaN, and 1.0m and 1.00m each encode once, a DateTime keeps
/// only its ticks, and a DateTimeOffset only its UTC instant. Strings must be valid UTF-16, and sort by their
/// Unicode code points. An integer holds at most 255 bytes.
/// </para>
/// <para>
/// Each position of a key layout needs one type and one direction. A value written there both ascending and
/// descending, or as an integer and as a string, is two different keys, and the two sort apart.
/// </para>
/// <para>
/// <see cref="Create{T1}(T1)"/> and its overloads build keys of up to eight elements in one call, and
/// <see cref="CompositeKeyBuilder"/> builds keys of any number. Every reading member reads keys of any length.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var entry = CompositeKey.Create("by-customer", customer, orderId);
/// var bob = CompositeKey.Range(CompositeKey.Create("by-customer", "bob"));
/// var id = CompositeKey.Get&lt;long&gt;(entry, 2);
/// </code>
/// </example>
public static class CompositeKey
{
  /// <summary>Encodes a key of one element.</summary>
  /// <exception cref="NotSupportedException">Composite keys do not support the element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A BigInteger element is wider than 255 bytes.</exception>
  public static byte[] Create<T1>(T1 first)
  {
    var size = new CompositeKeyWriter();
    size.Append(first);
    var key = new byte[size.Length];
    var writer = new CompositeKeyWriter(key, 0);
    writer.Append(first);
    return key;
  }

  /// <summary>Encodes a key of 2 elements.</summary>
  /// <exception cref="NotSupportedException">Composite keys do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A BigInteger element is wider than 255 bytes.</exception>
  public static byte[] Create<T1, T2>(T1 first, T2 second)
  {
    var size = new CompositeKeyWriter();
    size.Append(first);
    size.Append(second);
    var key = new byte[size.Length];
    var writer = new CompositeKeyWriter(key, 0);
    writer.Append(first);
    writer.Append(second);
    return key;
  }

  /// <summary>Encodes a key of 3 elements.</summary>
  /// <exception cref="NotSupportedException">Composite keys do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A BigInteger element is wider than 255 bytes.</exception>
  public static byte[] Create<T1, T2, T3>(T1 first, T2 second, T3 third)
  {
    var size = new CompositeKeyWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    var key = new byte[size.Length];
    var writer = new CompositeKeyWriter(key, 0);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    return key;
  }

  /// <summary>Encodes a key of 4 elements.</summary>
  /// <exception cref="NotSupportedException">Composite keys do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A BigInteger element is wider than 255 bytes.</exception>
  public static byte[] Create<T1, T2, T3, T4>(T1 first, T2 second, T3 third, T4 fourth)
  {
    var size = new CompositeKeyWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    size.Append(fourth);
    var key = new byte[size.Length];
    var writer = new CompositeKeyWriter(key, 0);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    writer.Append(fourth);
    return key;
  }

  /// <summary>Encodes a key of 5 elements.</summary>
  /// <exception cref="NotSupportedException">Composite keys do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A BigInteger element is wider than 255 bytes.</exception>
  public static byte[] Create<T1, T2, T3, T4, T5>(T1 first, T2 second, T3 third, T4 fourth, T5 fifth)
  {
    var size = new CompositeKeyWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    size.Append(fourth);
    size.Append(fifth);
    var key = new byte[size.Length];
    var writer = new CompositeKeyWriter(key, 0);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    writer.Append(fourth);
    writer.Append(fifth);
    return key;
  }

  /// <summary>Encodes a key of 6 elements.</summary>
  /// <exception cref="NotSupportedException">Composite keys do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A BigInteger element is wider than 255 bytes.</exception>
  public static byte[] Create<T1, T2, T3, T4, T5, T6>(T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth)
  {
    var size = new CompositeKeyWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    size.Append(fourth);
    size.Append(fifth);
    size.Append(sixth);
    var key = new byte[size.Length];
    var writer = new CompositeKeyWriter(key, 0);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    writer.Append(fourth);
    writer.Append(fifth);
    writer.Append(sixth);
    return key;
  }

  /// <summary>Encodes a key of 7 elements.</summary>
  /// <exception cref="NotSupportedException">Composite keys do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A BigInteger element is wider than 255 bytes.</exception>
  public static byte[] Create<T1, T2, T3, T4, T5, T6, T7>(
      T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth, T7 seventh)
  {
    var size = new CompositeKeyWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    size.Append(fourth);
    size.Append(fifth);
    size.Append(sixth);
    size.Append(seventh);
    var key = new byte[size.Length];
    var writer = new CompositeKeyWriter(key, 0);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    writer.Append(fourth);
    writer.Append(fifth);
    writer.Append(sixth);
    writer.Append(seventh);
    return key;
  }

  /// <summary>Encodes a key of 8 elements. <see cref="CompositeKeyBuilder"/> builds longer keys.</summary>
  /// <exception cref="NotSupportedException">Composite keys do not support an element's type.</exception>
  /// <exception cref="ArgumentException">A string element is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A BigInteger element is wider than 255 bytes.</exception>
  public static byte[] Create<T1, T2, T3, T4, T5, T6, T7, T8>(
      T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth, T7 seventh, T8 eighth)
  {
    var size = new CompositeKeyWriter();
    size.Append(first);
    size.Append(second);
    size.Append(third);
    size.Append(fourth);
    size.Append(fifth);
    size.Append(sixth);
    size.Append(seventh);
    size.Append(eighth);
    var key = new byte[size.Length];
    var writer = new CompositeKeyWriter(key, 0);
    writer.Append(first);
    writer.Append(second);
    writer.Append(third);
    writer.Append(fourth);
    writer.Append(fifth);
    writer.Append(sixth);
    writer.Append(seventh);
    writer.Append(eighth);
    return key;
  }

  /// <summary>An element that sorts in descending order, for <see cref="Create{T1}(T1)"/> and its overloads.</summary>
  public static Descending<T> Descending<T>(T value) => new(value);

  /// <summary>Reads a key's first element; any elements after it are left unread.</summary>
  /// <exception cref="ArgumentException">The key has no elements, or its bytes are not a composite key.</exception>
  /// <exception cref="InvalidCastException">The element has another type.</exception>
  /// <exception cref="OverflowException">The type cannot hold the element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support the type.</exception>
  public static T1 Decode<T1>(ReadOnlySpan<byte> key)
  {
    var reader = new CompositeKeyReader(key);
    return Next<T1>(ref reader, 1, key);
  }

  /// <summary>Reads a key's first 2 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The key has fewer elements, or its bytes are not a composite key.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static (T1, T2) Decode<T1, T2>(ReadOnlySpan<byte> key)
  {
    var reader = new CompositeKeyReader(key);
    return (Next<T1>(ref reader, 2, key), Next<T2>(ref reader, 2, key));
  }

  /// <summary>Reads a key's first 3 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The key has fewer elements, or its bytes are not a composite key.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static (T1, T2, T3) Decode<T1, T2, T3>(ReadOnlySpan<byte> key)
  {
    var reader = new CompositeKeyReader(key);
    return (Next<T1>(ref reader, 3, key), Next<T2>(ref reader, 3, key), Next<T3>(ref reader, 3, key));
  }

  /// <summary>Reads a key's first 4 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The key has fewer elements, or its bytes are not a composite key.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static (T1, T2, T3, T4) Decode<T1, T2, T3, T4>(ReadOnlySpan<byte> key)
  {
    var reader = new CompositeKeyReader(key);
    return (Next<T1>(ref reader, 4, key), Next<T2>(ref reader, 4, key), Next<T3>(ref reader, 4, key),
        Next<T4>(ref reader, 4, key));
  }

  /// <summary>Reads a key's first 5 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The key has fewer elements, or its bytes are not a composite key.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static (T1, T2, T3, T4, T5) Decode<T1, T2, T3, T4, T5>(ReadOnlySpan<byte> key)
  {
    var reader = new CompositeKeyReader(key);
    return (Next<T1>(ref reader, 5, key), Next<T2>(ref reader, 5, key), Next<T3>(ref reader, 5, key),
        Next<T4>(ref reader, 5, key), Next<T5>(ref reader, 5, key));
  }

  /// <summary>Reads a key's first 6 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The key has fewer elements, or its bytes are not a composite key.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static (T1, T2, T3, T4, T5, T6) Decode<T1, T2, T3, T4, T5, T6>(ReadOnlySpan<byte> key)
  {
    var reader = new CompositeKeyReader(key);
    return (Next<T1>(ref reader, 6, key), Next<T2>(ref reader, 6, key), Next<T3>(ref reader, 6, key),
        Next<T4>(ref reader, 6, key), Next<T5>(ref reader, 6, key), Next<T6>(ref reader, 6, key));
  }

  /// <summary>Reads a key's first 7 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The key has fewer elements, or its bytes are not a composite key.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static (T1, T2, T3, T4, T5, T6, T7) Decode<T1, T2, T3, T4, T5, T6, T7>(ReadOnlySpan<byte> key)
  {
    var reader = new CompositeKeyReader(key);
    return (Next<T1>(ref reader, 7, key), Next<T2>(ref reader, 7, key), Next<T3>(ref reader, 7, key),
        Next<T4>(ref reader, 7, key), Next<T5>(ref reader, 7, key), Next<T6>(ref reader, 7, key),
        Next<T7>(ref reader, 7, key));
  }

  /// <summary>Reads a key's first 8 elements; any elements after them are left unread.</summary>
  /// <exception cref="ArgumentException">The key has fewer elements, or its bytes are not a composite key.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static (T1, T2, T3, T4, T5, T6, T7, T8) Decode<T1, T2, T3, T4, T5, T6, T7, T8>(ReadOnlySpan<byte> key)
  {
    var reader = new CompositeKeyReader(key);
    return (Next<T1>(ref reader, 8, key), Next<T2>(ref reader, 8, key), Next<T3>(ref reader, 8, key),
        Next<T4>(ref reader, 8, key), Next<T5>(ref reader, 8, key), Next<T6>(ref reader, 8, key),
        Next<T7>(ref reader, 8, key), Next<T8>(ref reader, 8, key));
  }

  static T Next<T>(ref CompositeKeyReader reader, int expected, ReadOnlySpan<byte> key)
  {
    if (!reader.Next())
      throw new ArgumentException($"The key has {ElementCount(key)} elements, fewer than {expected}.", nameof(key));
    return reader.Read<T>();
  }

  /// <summary>Reads one element of a key.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The key has no element at the index.</exception>
  /// <exception cref="ArgumentException">The key's bytes are not a composite key.</exception>
  /// <exception cref="InvalidCastException">The element has another type.</exception>
  /// <exception cref="OverflowException">The type cannot hold the element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support the type.</exception>
  public static T Get<T>(ReadOnlySpan<byte> key, int index) => At(key, index).Read<T>();

  /// <summary>The number of elements in a key.</summary>
  /// <exception cref="ArgumentException">The key's bytes are not a composite key.</exception>
  public static int ElementCount(ReadOnlySpan<byte> key)
  {
    var reader = new CompositeKeyReader(key);
    var count = 0;
    while (reader.Next())
      ++count;
    return count;
  }

  /// <summary>The type of one element of a key.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The key has no element at the index.</exception>
  /// <exception cref="ArgumentException">The key's bytes are not a composite key.</exception>
  public static CompositeElementType ElementType(ReadOnlySpan<byte> key, int index) => At(key, index).ElementType;

  /// <summary>Whether one element of a key sorts in descending order.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The key has no element at the index.</exception>
  /// <exception cref="ArgumentException">The key's bytes are not a composite key.</exception>
  public static bool IsDescending(ReadOnlySpan<byte> key, int index) => At(key, index).IsDescending;

  /// <summary>
  /// The encoded bytes of one element of a key: a slice of the key, which can be appended to another key as it
  /// is.
  /// </summary>
  /// <exception cref="ArgumentOutOfRangeException">The key has no element at the index.</exception>
  /// <exception cref="ArgumentException">The key's bytes are not a composite key.</exception>
  public static ReadOnlySpan<byte> ElementBytes(ReadOnlySpan<byte> key, int index) => At(key, index).ElementBytes;

  /// <summary>
  /// The encoded bytes of a run of elements of a key, such as the primary key at the end of an index entry: a
  /// slice of the key, which is itself the key of those elements.
  /// </summary>
  /// <param name="key">The key.</param>
  /// <param name="start">The index of the run's first element.</param>
  /// <param name="count">The number of elements in the run.</param>
  /// <exception cref="ArgumentOutOfRangeException">The key does not have the run of elements.</exception>
  /// <exception cref="ArgumentException">The key's bytes are not a composite key.</exception>
  public static ReadOnlySpan<byte> ElementBytes(ReadOnlySpan<byte> key, int start, int count)
  {
    if (start < 0)
      throw new ArgumentOutOfRangeException(nameof(start), start, "A run cannot start before the first element.");
    if (count < 0)
      throw new ArgumentOutOfRangeException(nameof(count), count, "A run cannot have a negative number of elements.");
    var reader = new CompositeKeyReader(key);
    var from = 0;
    for (var i = 0; i < start; ++i)
    {
      if (!reader.Next())
        throw new ArgumentOutOfRangeException(nameof(start), start, $"The key has {i} elements.");
      from = reader.Offset + reader.ElementBytes.Length;
    }
    var to = from;
    for (var i = 0; i < count; ++i)
    {
      if (!reader.Next())
        throw new ArgumentOutOfRangeException(nameof(count), count, $"The key has {start + i} elements.");
      to = reader.Offset + reader.ElementBytes.Length;
    }
    return key.Slice(from, to - from);
  }

  /// <summary>
  /// Finds where every element of a key starts in one pass, for direct access afterwards: element i is
  /// key[offsets[i]..offsets[i + 1]].
  /// </summary>
  /// <param name="key">The key.</param>
  /// <param name="offsets">Room for an offset for each element and one for the key's end.</param>
  /// <returns>The number of elements.</returns>
  /// <exception cref="ArgumentException">
  /// The offsets have no room for every element and the end, or the key's bytes are not a composite key.
  /// </exception>
  public static int Offsets(ReadOnlySpan<byte> key, Span<int> offsets)
  {
    if (offsets.Length == 0)
      throw new ArgumentException("The offsets have no room for the key's end.", nameof(offsets));
    var reader = new CompositeKeyReader(key);
    var count = 0;
    while (reader.Next())
    {
      if (count == offsets.Length - 1)
        throw new ArgumentException(
            $"The offsets have room for {count} elements and the end, and the key has more.", nameof(offsets));
      offsets[count++] = reader.Offset;
    }
    offsets[count] = key.Length;
    return count;
  }

  /// <summary>
  /// The bounds of the keys under whole elements: every key that starts with the elements of a prefix, the prefix
  /// itself among them. They are inclusive bounds for a range scan or a seek in byte order, and they match whole
  /// elements: the prefix ("bob") does not hold ("bobby"), which <see cref="BeginsWith(ReadOnlySpan{byte}, string, CompositeKeyOrder)"/> does.
  /// </summary>
  /// <exception cref="ArgumentException">The prefix's bytes are not a composite key.</exception>
  public static (byte[] From, byte[] To) Range(ReadOnlySpan<byte> prefix)
  {
    CheckElements(prefix);
    return Bounds(prefix.ToArray(), 1);
  }

  /// <summary>
  /// The bounds of the keys whose next element, after the elements of a prefix, is a string that begins with given
  /// text, such as every name that begins with "bo": inclusive bounds for a range scan or a seek in byte order.
  /// </summary>
  /// <param name="prefix">The whole elements before the string; none for keys that begin with the string.</param>
  /// <param name="text">The text the string begins with; a string that is the text itself is among them.</param>
  /// <param name="order">The order of the string element.</param>
  /// <exception cref="ArgumentException">The prefix's bytes are not a composite key, or the text is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  public static (byte[] From, byte[] To) BeginsWith(
      ReadOnlySpan<byte> prefix, string text, CompositeKeyOrder order = CompositeKeyOrder.Ascending)
  {
    ArgumentNullException.ThrowIfNull(text);
    CheckElements(prefix);
    var count = StrictUtf8.GetByteCount(text);
    byte[] rented = null;
    var bytes = count <= 256 ? stackalloc byte[count] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(count)).AsSpan(0, count);
    try
    {
      StrictUtf8.GetBytes(text, bytes);
      return BeginsWithBounds(prefix, StringTag, bytes, order);
    }
    finally
    {
      if (rented != null)
        System.Buffers.ArrayPool<byte>.Shared.Return(rented);
    }
  }

  /// <summary>
  /// The bounds of the keys whose next element, after the elements of a prefix, is a bytes element that begins
  /// with given bytes, such as every hash that begins with a byte: inclusive bounds for a range scan or a seek in
  /// byte order.
  /// </summary>
  /// <param name="prefix">The whole elements before the bytes element; none for keys that begin with it.</param>
  /// <param name="bytes">The bytes the element begins with; an element of the bytes themselves is among them.</param>
  /// <param name="order">The order of the bytes element.</param>
  /// <exception cref="ArgumentException">The prefix's bytes are not a composite key.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  public static (byte[] From, byte[] To) BeginsWith(
      ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> bytes, CompositeKeyOrder order = CompositeKeyOrder.Ascending)
  {
    CheckElements(prefix);
    return BeginsWithBounds(prefix, BytesTag, bytes, order);
  }

  /// <summary>
  /// The bounds of the keys whose next element, a string or bytes, begins with given bytes: the prefix, the tag and
  /// the escaped bytes, without the terminator that would end the element, up to the first bytes after every key
  /// that goes on from there.
  /// </summary>
  static (byte[] From, byte[] To) BeginsWithBounds(ReadOnlySpan<byte> prefix, byte tag, ReadOnlySpan<byte> bytes, CompositeKeyOrder order)
  {
    var descending = Descends(order);
#if NET8_0_OR_GREATER
    var zeros = bytes.Count((byte)0);
#else
    var zeros = 0;
    foreach (var b in bytes)
    {
      if (b == 0)
        ++zeros;
    }
#endif
    var from = new byte[prefix.Length + 1 + bytes.Length + zeros];
    prefix.CopyTo(from);
    var at = prefix.Length;
    from[at++] = tag;
    foreach (var b in bytes)
    {
      from[at++] = b;
      if (b == 0)
        from[at++] = Escape;
    }
    if (descending)
    {
      foreach (ref var b in from.AsSpan(prefix.Length))
        b = (byte)~b;
    }
    // The element goes on with any bytes, 0xFF among them, so the bound is the first bytes after every key that
    // starts with these: their last byte below 0xFF, incremented, and nothing after it. The tag is below 0xFF,
    // and the bound ends within the element, so it is no key itself.
#if NET7_0_OR_GREATER
    var last = from.AsSpan().LastIndexOfAnyExcept(PrefixEnd);
#else
    var last = from.Length - 1;
    while (from[last] == PrefixEnd)
      --last;
#endif
    var to = from.AsSpan(0, last + 1).ToArray();
    ++to[last];
    return (from, to);
  }

  /// <summary>
  /// The bounds of the keys that start with bytes the caller owns: the bytes themselves, and the bytes followed by
  /// a number of 0xFF bytes, which no element starts with.
  /// </summary>
  internal static (byte[] From, byte[] To) Bounds(byte[] from, int ends)
  {
    var to = new byte[from.Length + ends];
    from.CopyTo(to, 0);
    to.AsSpan(from.Length).Fill(PrefixEnd);
    return (from, to);
  }

  /// <exception cref="ArgumentOutOfRangeException">The order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  internal static bool Descends(CompositeKeyOrder order) => order switch
  {
    CompositeKeyOrder.Ascending => false,
    CompositeKeyOrder.Descending => true,
    _ => throw new ArgumentOutOfRangeException(nameof(order), order, "An order is ascending or descending.")
  };

  /// <summary>Checks that bytes are whole composite key elements.</summary>
  /// <exception cref="ArgumentException">The bytes are not a composite key.</exception>
  internal static void CheckElements(ReadOnlySpan<byte> key)
  {
    var reader = new CompositeKeyReader(key);
    while (reader.Next())
    {
    }
  }

  /// <summary>
  /// Whether bytes are a composite key: elements in their one encoding, with strings of valid UTF-8. Other
  /// bytes can happen to be one too, so it tells composite keys from other bytes only as far as their bytes do.
  /// </summary>
  public static bool IsValid(ReadOnlySpan<byte> key)
  {
    var offset = 0;
    while (offset < key.Length)
    {
      var length = Measure(key, offset);
      if (length < 0)
        return false;
      var tag = AscendingTag(key[offset], out var mask);
      if (tag == StringTag && !IsValidUtf8(key.Slice(offset + 1, length - 3), mask))
        return false;
      offset += length;
    }
    return true;
  }

  static CompositeKeyReader At(ReadOnlySpan<byte> key, int index)
  {
    if (index < 0)
      throw new ArgumentOutOfRangeException(nameof(index), index, "An element index cannot be negative.");
    var reader = new CompositeKeyReader(key);
    for (var i = 0; i <= index; ++i)
    {
      if (!reader.Next())
        throw new ArgumentOutOfRangeException(nameof(index), index, $"The key has {i} elements.");
    }
    return reader;
  }
}
