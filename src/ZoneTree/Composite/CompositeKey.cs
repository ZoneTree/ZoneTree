using System.Text;
using static ZoneTree.Composite.CompositeKeyFormat;

namespace ZoneTree.Composite;

/// <summary>
/// Encodes several values into one byte-array key whose bytes sort as the values do, element by element, and
/// reads them back. The keys sort with <see cref="Comparers.BytesLexicographicComparerAscending"/>, the default
/// comparer of byte-array keys, or in reverse with <see cref="Comparers.BytesLexicographicComparerDescending"/>.
/// </summary>
/// <remarks>
/// <para>
/// A key is its elements one after another, with no header, so two keys joined end to end are the key of their
/// elements together, and a key's bytes start every key that extends it. One tree can hold rows and the entries
/// of several indexes, each under a leading element of its own. <see cref="Range"/> makes the range of the keys
/// that start with given elements, <see cref="Between"/> that of the keys from one prefix through another,
/// <see cref="AtLeast"/>, <see cref="GreaterThan"/>, <see cref="AtMost"/> and <see cref="LessThan"/> those of the
/// keys on one side of a value, and <see cref="BeginsWith(ReadOnlySpan{byte}, string, CompositeKeyOrder)"/> that of
/// the keys whose next string begins with given text.
/// </para>
/// <para>
/// Every range is a <see cref="CompositeKeyRange"/>. A forward scan seeks its lower bound and a reverse scan its
/// upper bound, and each reads while <see cref="CompositeKeyRange.Contains"/> holds.
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
/// <see cref="CompositeKeyBuilder"/> builds keys of any number. Every reading member reads keys of any length,
/// and those that read elements as types have a Try form, which returns false for a key that does not hold the
/// types instead of throwing. <see cref="Format"/> shows any key as text.
/// </para>
/// <para>
/// A key is a byte array, and <c>==</c> on arrays compares references. For sets, dictionaries and sorts of keys,
/// <see cref="ComparerAscending"/> and <see cref="ComparerDescending"/> compare keys in the order of the tree's
/// comparer of the same direction, and <see cref="TextComparerAscending"/> and <see cref="TextComparerDescending"/>
/// order strings as a forward scan returns them.
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

  /// <summary>
  /// Compares keys in the order of <see cref="Comparers.BytesLexicographicComparerAscending"/>, the default comparer
  /// of byte-array keys, for sets, dictionaries and sorts of keys in memory, where <c>==</c> on the arrays would
  /// compare references.
  /// </summary>
  public static CompositeKeyComparerAscending ComparerAscending => CompositeKeyComparerAscending.Instance;

  /// <summary>
  /// Compares keys in the order of <see cref="Comparers.BytesLexicographicComparerDescending"/>. It finds the same
  /// keys equal as <see cref="ComparerAscending"/> does.
  /// </summary>
  public static CompositeKeyComparerDescending ComparerDescending => CompositeKeyComparerDescending.Instance;

  /// <summary>
  /// Orders strings by their Unicode code points, a null first, as a forward scan returns an ascending string element
  /// on a tree with <see cref="Comparers.BytesLexicographicComparerAscending"/>, or a descending one on a tree with
  /// <see cref="Comparers.BytesLexicographicComparerDescending"/>. <see cref="StringComparer.Ordinal"/> orders UTF-16
  /// code units instead, which puts a character above U+FFFF before one from U+E000 to U+FFFF.
  /// </summary>
  public static StringComparer TextComparerAscending => CompositeTextComparerAscending.Instance;

  /// <summary>
  /// Orders strings in the reverse of <see cref="TextComparerAscending"/>, a null last, as a forward scan returns a
  /// descending string element on a tree with <see cref="Comparers.BytesLexicographicComparerAscending"/>, or an
  /// ascending one on a tree with <see cref="Comparers.BytesLexicographicComparerDescending"/>.
  /// </summary>
  public static StringComparer TextComparerDescending => CompositeTextComparerDescending.Instance;

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

  /// <summary>Reads a key's first element, as <see cref="Decode{T1}(ReadOnlySpan{byte})"/> does, unless the key does not hold it.</summary>
  /// <returns>
  /// False if the key has no elements, its bytes are not a composite key up to the element, the element has
  /// another type, the type cannot hold its value exactly, or a string element's bytes are not valid UTF-8.
  /// </returns>
  /// <exception cref="NotSupportedException">Composite keys do not support the type.</exception>
  public static bool TryDecode<T1>(ReadOnlySpan<byte> key, out T1 first)
  {
    CompositeElementReading.CheckReadable<T1>();
    var reader = new CompositeKeyReader(key);
    if (TryNext(ref reader, out first))
      return true;
    first = default;
    return false;
  }

  /// <summary>Reads a key's first 2 elements, as <see cref="Decode{T1, T2}(ReadOnlySpan{byte})"/> does, unless the key does not hold them.</summary>
  /// <returns>
  /// False if the key has fewer elements, its bytes are not a composite key up to them, an element has another
  /// type, a type cannot hold its element's value exactly, or a string element's bytes are not valid UTF-8.
  /// </returns>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static bool TryDecode<T1, T2>(ReadOnlySpan<byte> key, out (T1, T2) elements)
  {
    CompositeElementReading.CheckReadable<T1>();
    CompositeElementReading.CheckReadable<T2>();
    var reader = new CompositeKeyReader(key);
    elements = default;
    if (TryNext(ref reader, out elements.Item1) && TryNext(ref reader, out elements.Item2))
      return true;
    elements = default;
    return false;
  }

  /// <summary>Reads a key's first 3 elements, as <see cref="Decode{T1, T2, T3}(ReadOnlySpan{byte})"/> does, unless the key does not hold them.</summary>
  /// <returns>
  /// False if the key has fewer elements, its bytes are not a composite key up to them, an element has another
  /// type, a type cannot hold its element's value exactly, or a string element's bytes are not valid UTF-8.
  /// </returns>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static bool TryDecode<T1, T2, T3>(ReadOnlySpan<byte> key, out (T1, T2, T3) elements)
  {
    CompositeElementReading.CheckReadable<T1>();
    CompositeElementReading.CheckReadable<T2>();
    CompositeElementReading.CheckReadable<T3>();
    var reader = new CompositeKeyReader(key);
    elements = default;
    if (TryNext(ref reader, out elements.Item1) && TryNext(ref reader, out elements.Item2) &&
        TryNext(ref reader, out elements.Item3))
      return true;
    elements = default;
    return false;
  }

  /// <summary>Reads a key's first 4 elements, as <see cref="Decode{T1, T2, T3, T4}(ReadOnlySpan{byte})"/> does, unless the key does not hold them.</summary>
  /// <returns>
  /// False if the key has fewer elements, its bytes are not a composite key up to them, an element has another
  /// type, a type cannot hold its element's value exactly, or a string element's bytes are not valid UTF-8.
  /// </returns>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static bool TryDecode<T1, T2, T3, T4>(ReadOnlySpan<byte> key, out (T1, T2, T3, T4) elements)
  {
    CompositeElementReading.CheckReadable<T1>();
    CompositeElementReading.CheckReadable<T2>();
    CompositeElementReading.CheckReadable<T3>();
    CompositeElementReading.CheckReadable<T4>();
    var reader = new CompositeKeyReader(key);
    elements = default;
    if (TryNext(ref reader, out elements.Item1) && TryNext(ref reader, out elements.Item2) &&
        TryNext(ref reader, out elements.Item3) && TryNext(ref reader, out elements.Item4))
      return true;
    elements = default;
    return false;
  }

  /// <summary>Reads a key's first 5 elements, as <see cref="Decode{T1, T2, T3, T4, T5}(ReadOnlySpan{byte})"/> does, unless the key does not hold them.</summary>
  /// <returns>
  /// False if the key has fewer elements, its bytes are not a composite key up to them, an element has another
  /// type, a type cannot hold its element's value exactly, or a string element's bytes are not valid UTF-8.
  /// </returns>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static bool TryDecode<T1, T2, T3, T4, T5>(ReadOnlySpan<byte> key, out (T1, T2, T3, T4, T5) elements)
  {
    CompositeElementReading.CheckReadable<T1>();
    CompositeElementReading.CheckReadable<T2>();
    CompositeElementReading.CheckReadable<T3>();
    CompositeElementReading.CheckReadable<T4>();
    CompositeElementReading.CheckReadable<T5>();
    var reader = new CompositeKeyReader(key);
    elements = default;
    if (TryNext(ref reader, out elements.Item1) && TryNext(ref reader, out elements.Item2) &&
        TryNext(ref reader, out elements.Item3) && TryNext(ref reader, out elements.Item4) &&
        TryNext(ref reader, out elements.Item5))
      return true;
    elements = default;
    return false;
  }

  /// <summary>Reads a key's first 6 elements, as <see cref="Decode{T1, T2, T3, T4, T5, T6}(ReadOnlySpan{byte})"/> does, unless the key does not hold them.</summary>
  /// <returns>
  /// False if the key has fewer elements, its bytes are not a composite key up to them, an element has another
  /// type, a type cannot hold its element's value exactly, or a string element's bytes are not valid UTF-8.
  /// </returns>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static bool TryDecode<T1, T2, T3, T4, T5, T6>(ReadOnlySpan<byte> key, out (T1, T2, T3, T4, T5, T6) elements)
  {
    CompositeElementReading.CheckReadable<T1>();
    CompositeElementReading.CheckReadable<T2>();
    CompositeElementReading.CheckReadable<T3>();
    CompositeElementReading.CheckReadable<T4>();
    CompositeElementReading.CheckReadable<T5>();
    CompositeElementReading.CheckReadable<T6>();
    var reader = new CompositeKeyReader(key);
    elements = default;
    if (TryNext(ref reader, out elements.Item1) && TryNext(ref reader, out elements.Item2) &&
        TryNext(ref reader, out elements.Item3) && TryNext(ref reader, out elements.Item4) &&
        TryNext(ref reader, out elements.Item5) && TryNext(ref reader, out elements.Item6))
      return true;
    elements = default;
    return false;
  }

  /// <summary>Reads a key's first 7 elements, as <see cref="Decode{T1, T2, T3, T4, T5, T6, T7}(ReadOnlySpan{byte})"/> does, unless the key does not hold them.</summary>
  /// <returns>
  /// False if the key has fewer elements, its bytes are not a composite key up to them, an element has another
  /// type, a type cannot hold its element's value exactly, or a string element's bytes are not valid UTF-8.
  /// </returns>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static bool TryDecode<T1, T2, T3, T4, T5, T6, T7>(ReadOnlySpan<byte> key, out (T1, T2, T3, T4, T5, T6, T7) elements)
  {
    CompositeElementReading.CheckReadable<T1>();
    CompositeElementReading.CheckReadable<T2>();
    CompositeElementReading.CheckReadable<T3>();
    CompositeElementReading.CheckReadable<T4>();
    CompositeElementReading.CheckReadable<T5>();
    CompositeElementReading.CheckReadable<T6>();
    CompositeElementReading.CheckReadable<T7>();
    var reader = new CompositeKeyReader(key);
    elements = default;
    if (TryNext(ref reader, out elements.Item1) && TryNext(ref reader, out elements.Item2) &&
        TryNext(ref reader, out elements.Item3) && TryNext(ref reader, out elements.Item4) &&
        TryNext(ref reader, out elements.Item5) && TryNext(ref reader, out elements.Item6) &&
        TryNext(ref reader, out elements.Item7))
      return true;
    elements = default;
    return false;
  }

  /// <summary>Reads a key's first 8 elements, as <see cref="Decode{T1, T2, T3, T4, T5, T6, T7, T8}(ReadOnlySpan{byte})"/> does, unless the key does not hold them.</summary>
  /// <returns>
  /// False if the key has fewer elements, its bytes are not a composite key up to them, an element has another
  /// type, a type cannot hold its element's value exactly, or a string element's bytes are not valid UTF-8.
  /// </returns>
  /// <exception cref="NotSupportedException">Composite keys do not support a type.</exception>
  public static bool TryDecode<T1, T2, T3, T4, T5, T6, T7, T8>(
      ReadOnlySpan<byte> key, out (T1, T2, T3, T4, T5, T6, T7, T8) elements)
  {
    CompositeElementReading.CheckReadable<T1>();
    CompositeElementReading.CheckReadable<T2>();
    CompositeElementReading.CheckReadable<T3>();
    CompositeElementReading.CheckReadable<T4>();
    CompositeElementReading.CheckReadable<T5>();
    CompositeElementReading.CheckReadable<T6>();
    CompositeElementReading.CheckReadable<T7>();
    CompositeElementReading.CheckReadable<T8>();
    var reader = new CompositeKeyReader(key);
    elements = default;
    if (TryNext(ref reader, out elements.Item1) && TryNext(ref reader, out elements.Item2) &&
        TryNext(ref reader, out elements.Item3) && TryNext(ref reader, out elements.Item4) &&
        TryNext(ref reader, out elements.Item5) && TryNext(ref reader, out elements.Item6) &&
        TryNext(ref reader, out elements.Item7) && TryNext(ref reader, out elements.Item8))
      return true;
    elements = default;
    return false;
  }

  static bool TryNext<T>(ref CompositeKeyReader reader, out T value)
  {
    if (reader.TryNext())
      return reader.TryRead(out value);
    value = default;
    return false;
  }

  /// <summary>Reads one element of a key.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The key has no element at the index.</exception>
  /// <exception cref="ArgumentException">The key's bytes are not a composite key.</exception>
  /// <exception cref="InvalidCastException">The element has another type.</exception>
  /// <exception cref="OverflowException">The type cannot hold the element's value exactly.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support the type.</exception>
  public static T Get<T>(ReadOnlySpan<byte> key, int index) => At(key, index).Read<T>();

  /// <summary>Reads one element of a key, as <see cref="Get{T}"/> does, unless the key does not hold it.</summary>
  /// <returns>
  /// False if the key has no element at the index, its bytes are not a composite key up to the element, the
  /// element has another type, the type cannot hold its value exactly, or a string element's bytes are not valid
  /// UTF-8.
  /// </returns>
  /// <exception cref="NotSupportedException">Composite keys do not support the type.</exception>
  public static bool TryGet<T>(ReadOnlySpan<byte> key, int index, out T value)
  {
    CompositeElementReading.CheckReadable<T>();
    var reader = new CompositeKeyReader(key);
    var found = index >= 0;
    for (var i = 0; found && i <= index; ++i)
      found = reader.TryNext();
    if (found)
      return reader.TryRead(out value);
    value = default;
    return false;
  }

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
  /// The range of the keys under whole elements: every key that starts with the elements of a prefix, the prefix
  /// itself among them. It matches whole elements: the prefix ("bob") does not hold ("bobby"), which
  /// <see cref="BeginsWith(ReadOnlySpan{byte}, string, CompositeKeyOrder)"/> does.
  /// </summary>
  /// <exception cref="ArgumentException">The prefix's bytes are not a composite key.</exception>
  public static CompositeKeyRange Range(ReadOnlySpan<byte> prefix)
  {
    CheckElements(prefix);
    return RangeUnder(prefix.ToArray());
  }

  /// <summary>
  /// The range of the keys from one prefix through another. The prefixes usually share leading elements and differ
  /// in their last, such as ("bob", monday) and ("bob", friday) for bob's keys from monday through friday, or up to
  /// friday with <see cref="CompositeKeyBounds.ExcludeHigh"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Each prefix stands for the keys under its whole elements. The range starts where the keys under the prefix
  /// that sorts first start, or just after them where it is excluded, and it stops where the keys under the other
  /// stop, or just before them where it is excluded. Where one prefix starts the other, as ("bob") and
  /// ("bob", 5) do, the range runs from the first of bob's keys through those under ("bob", 5).
  /// </para>
  /// <para>
  /// The prefixes come in either order. A descending element puts the higher value first, so the range starts at
  /// whichever prefix sorts first in bytes, and the same call serves an ascending and a descending element.
  /// <see cref="CompositeKeyBounds"/> names the prefix it excludes by its argument, whichever sorts first.
  /// </para>
  /// </remarks>
  /// <param name="low">A prefix, usually the one of the lower value.</param>
  /// <param name="high">A prefix, usually the one of the higher value.</param>
  /// <param name="bounds">Which of the two prefixes the range includes.</param>
  /// <exception cref="ArgumentException">A prefix's bytes are not a composite key.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The bounds are not one of <see cref="CompositeKeyBounds"/>.</exception>
  public static CompositeKeyRange Between(
      ReadOnlySpan<byte> low, ReadOnlySpan<byte> high, CompositeKeyBounds bounds = CompositeKeyBounds.Inclusive)
  {
    CheckBounds(bounds);
    CheckElements(low);
    CheckElements(high);
    return RangeBetween(low, high, bounds);
  }

  /// <summary>
  /// The range of the keys from a low value on: those that start with the leading elements of a prefix and go on
  /// with an element at least its last, with any elements after it. A prefix of ("bob", 10) holds bob's keys from
  /// 10 on.
  /// </summary>
  /// <remarks>
  /// The range holds what a <see cref="Between"/> with that one end holds. Elements compare as keys sort: by value
  /// in either order, with a null below every value, so when the end's value is not null, nulls are in AtMost and
  /// LessThan, and not in AtLeast or GreaterThan. On a descending element, the greater values sort first.
  /// </remarks>
  /// <param name="low">
  /// A prefix: the leading elements, then the value the range starts at. Every key is under an empty one, so the
  /// range then holds every key.
  /// </param>
  /// <exception cref="ArgumentException">The prefix's bytes are not a composite key.</exception>
  public static CompositeKeyRange AtLeast(ReadOnlySpan<byte> low)
  {
    CheckElements(low);
    return OneSided(low, lowEnd: true, inclusive: true);
  }

  /// <summary>
  /// The range of the keys after a low value: those that start with the leading elements of a prefix and go on with
  /// an element greater than its last, with any elements after it. A prefix of ("bob", 10) holds bob's keys after
  /// 10.
  /// </summary>
  /// <remarks>
  /// The range holds what a <see cref="Between"/> with that one end holds. Elements compare as keys sort: by value
  /// in either order, with a null below every value, so when the end's value is not null, nulls are in AtMost and
  /// LessThan, and not in AtLeast or GreaterThan. On a descending element, the greater values sort first.
  /// </remarks>
  /// <param name="low">
  /// A prefix: the leading elements, then the value the range starts after. Every key is under an empty one, so the
  /// range then holds no key.
  /// </param>
  /// <exception cref="ArgumentException">The prefix's bytes are not a composite key.</exception>
  public static CompositeKeyRange GreaterThan(ReadOnlySpan<byte> low)
  {
    CheckElements(low);
    return OneSided(low, lowEnd: true, inclusive: false);
  }

  /// <summary>
  /// The range of the keys up to a high value: those that start with the leading elements of a prefix and go on
  /// with an element at most its last, with any elements after it. A prefix of ("bob", 10) holds bob's keys
  /// through 10.
  /// </summary>
  /// <remarks>
  /// The range holds what a <see cref="Between"/> with that one end holds. Elements compare as keys sort: by value
  /// in either order, with a null below every value, so when the end's value is not null, nulls are in AtMost and
  /// LessThan, and not in AtLeast or GreaterThan. On a descending element, the greater values sort first.
  /// </remarks>
  /// <param name="high">
  /// A prefix: the leading elements, then the value the range ends at. Every key is under an empty one, so the range
  /// then holds every key.
  /// </param>
  /// <exception cref="ArgumentException">The prefix's bytes are not a composite key.</exception>
  public static CompositeKeyRange AtMost(ReadOnlySpan<byte> high)
  {
    CheckElements(high);
    return OneSided(high, lowEnd: false, inclusive: true);
  }

  /// <summary>
  /// The range of the keys below a high value: those that start with the leading elements of a prefix and go on
  /// with an element less than its last, with any elements after it. A prefix of ("bob", 10) holds bob's keys
  /// before 10.
  /// </summary>
  /// <remarks>
  /// The range holds what a <see cref="Between"/> with that one end holds. Elements compare as keys sort: by value
  /// in either order, with a null below every value, so when the end's value is not null, nulls are in AtMost and
  /// LessThan, and not in AtLeast or GreaterThan. On a descending element, the greater values sort first.
  /// </remarks>
  /// <param name="high">
  /// A prefix: the leading elements, then the value the range ends before. Every key is under an empty one, so the
  /// range then holds no key.
  /// </param>
  /// <exception cref="ArgumentException">The prefix's bytes are not a composite key.</exception>
  public static CompositeKeyRange LessThan(ReadOnlySpan<byte> high)
  {
    CheckElements(high);
    return OneSided(high, lowEnd: false, inclusive: false);
  }

  /// <summary>
  /// The range of the keys whose next element, after the elements of a prefix, is a string that begins with given
  /// text, such as every name that begins with "bo".
  /// </summary>
  /// <param name="prefix">The whole elements before the string; none for keys that begin with the string.</param>
  /// <param name="text">The text the string begins with; a string that is the text itself is among them.</param>
  /// <param name="order">The order of the string element.</param>
  /// <exception cref="ArgumentException">The prefix's bytes are not a composite key, or the text is not valid UTF-16.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  public static CompositeKeyRange BeginsWith(
      ReadOnlySpan<byte> prefix, string text, CompositeKeyOrder order = CompositeKeyOrder.Ascending)
  {
    ArgumentNullException.ThrowIfNull(text);
    CheckElements(prefix);
    return BeginsWithText(prefix, text, Descends(order));
  }

  /// <summary>
  /// The range of the keys whose next element, after the elements of a prefix, is a bytes element that begins
  /// with given bytes, such as every hash that begins with a byte.
  /// </summary>
  /// <param name="prefix">The whole elements before the bytes element; none for keys that begin with it.</param>
  /// <param name="bytes">The bytes the element begins with; an element of the bytes themselves is among them.</param>
  /// <param name="order">The order of the bytes element.</param>
  /// <exception cref="ArgumentException">The prefix's bytes are not a composite key.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  public static CompositeKeyRange BeginsWith(
      ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> bytes, CompositeKeyOrder order = CompositeKeyOrder.Ascending)
  {
    CheckElements(prefix);
    return BeginsWithBytes(prefix, bytes, Descends(order));
  }

  /// <summary>
  /// The range of the keys whose next element, after a prefix the caller checked, is a string in the given order that
  /// begins with text.
  /// </summary>
  /// <exception cref="ArgumentException">The text is not valid UTF-16.</exception>
  internal static CompositeKeyRange BeginsWithText(ReadOnlySpan<byte> prefix, string text, bool descending)
  {
    var count = Utf8Length(text, "The text", nameof(text));
    byte[] rented = null;
    var bytes = count <= 256 ? stackalloc byte[count] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(count)).AsSpan(0, count);
    try
    {
      StrictUtf8.GetBytes(text, bytes);
      return BeginsWithRange(prefix, StringTag, bytes, descending);
    }
    finally
    {
      if (rented != null)
        System.Buffers.ArrayPool<byte>.Shared.Return(rented);
    }
  }

  /// <summary>
  /// The range of the keys whose next element, after a prefix the caller checked, is a bytes element in the given
  /// order that begins with bytes.
  /// </summary>
  internal static CompositeKeyRange BeginsWithBytes(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> bytes, bool descending)
      => BeginsWithRange(prefix, BytesTag, bytes, descending);

  /// <summary>
  /// The range of the keys whose next element, a string or bytes, begins with given bytes: from the prefix, the tag
  /// and the escaped bytes, without the terminator that would end the element, up to the first bytes after every
  /// key that goes on from there.
  /// </summary>
  static CompositeKeyRange BeginsWithRange(ReadOnlySpan<byte> prefix, byte tag, ReadOnlySpan<byte> bytes, bool descending)
  {
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
    var lower = new byte[prefix.Length + 1 + bytes.Length + zeros];
    prefix.CopyTo(lower);
    var at = prefix.Length;
    lower[at++] = tag;
    foreach (var b in bytes)
    {
      lower[at++] = b;
      if (b == 0)
        lower[at++] = Escape;
    }
    if (descending)
    {
      foreach (ref var b in lower.AsSpan(prefix.Length))
        b = (byte)~b;
    }
    // The element goes on with any bytes, 0xFF among them, so the upper bound is the first bytes after every key
    // that starts with these: their last byte below 0xFF, incremented, and nothing after it. The tag is below 0xFF,
    // so that byte is the tag or one after it. After the tag it ends within an element, which is no key; at the
    // tag it is the next tag, an upper bound as at the end of a type's elements.
#if NET7_0_OR_GREATER
    var last = lower.AsSpan().LastIndexOfAnyExcept(PrefixEnd);
#else
    var last = lower.Length - 1;
    while (lower[last] == PrefixEnd)
      --last;
#endif
    if (last == prefix.Length)
      return new(lower, UpperAtTag(prefix, (byte)(lower[last] + 1)));
    var upper = lower.AsSpan(0, last + 1).ToArray();
    ++upper[last];
    return new(lower, upper);
  }

  /// <summary>
  /// The range of the keys under elements the caller owns: from the elements up to their <see cref="UpperBound"/>.
  /// </summary>
  internal static CompositeKeyRange RangeUnder(byte[] elements) => new(elements, UpperBound(elements));

  /// <summary>
  /// The range from whichever of two prefixes sorts first through the other, each included or excluded as the
  /// bounds say, which the caller checked. A caller that owns a prefix's array passes it too, and an included
  /// first prefix then keeps its array rather than a copy.
  /// </summary>
  internal static CompositeKeyRange RangeBetween(ReadOnlySpan<byte> low, ReadOnlySpan<byte> high, CompositeKeyBounds bounds,
      byte[] ownedLow = null, byte[] ownedHigh = null)
  {
    var lowFirst = Ends(low, high, bounds, out var excludeFirst, out var excludeSecond);
    var first = lowFirst ? low : high;
    var second = lowFirst ? high : low;
    // Every key is under the empty prefix, so a range that ends below its keys holds none.
    if (excludeSecond && second.IsEmpty)
      return NoKeys();
    var lower = excludeFirst ? UpperBound(first) : (lowFirst ? ownedLow : ownedHigh) ?? first.ToArray();
    return new(lower, excludeSecond ? Below(second) : UpperBound(second));
  }

  /// <summary>
  /// The range of a <see cref="Between"/> with one end open, for an end of composite key elements the caller checked:
  /// the keys after the end's leading elements whose next element is on one side of the end's last in key order, by
  /// value in either order. In bytes, the range runs from the end to the last element at its position, or from the
  /// first element there to the end, as the element's order puts that side. An end stands for every key under it,
  /// as an end of <see cref="Between"/> does, so an empty end stands for every key. A caller that owns the end's
  /// array passes it too, and a range that starts at the end then keeps it.
  /// </summary>
  internal static CompositeKeyRange OneSided(ReadOnlySpan<byte> end, bool lowEnd, bool inclusive, byte[] ownedEnd = null)
  {
    // Every key is under the empty end: a range that includes its keys holds every key, and one that excludes
    // them holds none, on either side.
    if (end.IsEmpty)
      return inclusive ? RangeUnder([]) : NoKeys();
    var last = LastElement(end);
    var head = end.Slice(0, last);
    // A low end on an ascending element, and a high end on a descending one, keep the elements above it in bytes, up
    // to the end of the leading elements' keys; the others keep those below it, from the first element after them.
    if (lowEnd != (end[last] >= 0x80))
      return new(inclusive ? ownedEnd ?? end.ToArray() : UpperBound(end), UpperBound(head));
    return new(FirstAfter(head), inclusive ? UpperBound(end) : Below(end));
  }

  /// <summary>
  /// The range that holds no key: both bounds 0xFF, which starts no key, for a range that would otherwise end at the
  /// empty key, as nothing sorts below it.
  /// </summary>
  internal static CompositeKeyRange NoKeys() => new([PrefixEnd], [PrefixEnd]);

  /// <summary>
  /// Which of two prefixes sorts first, and whether the bounds exclude each in that order: an included prefix's
  /// keys run from it up to its <see cref="UpperBound"/>, so a range that excludes the first starts after its
  /// keys, at its upper bound, and one that excludes the second ends below its keys, at
  /// <see cref="CompositeKeyFormat.Below"/>.
  /// </summary>
  static bool Ends(ReadOnlySpan<byte> low, ReadOnlySpan<byte> high, CompositeKeyBounds bounds,
      out bool excludeFirst, out bool excludeSecond)
  {
    var lowFirst = low.SequenceCompareTo(high) <= 0;
    var excludeLow = (bounds & CompositeKeyBounds.ExcludeLow) != 0;
    var excludeHigh = (bounds & CompositeKeyBounds.ExcludeHigh) != 0;
    excludeFirst = lowFirst ? excludeLow : excludeHigh;
    excludeSecond = lowFirst ? excludeHigh : excludeLow;
    return lowFirst;
  }

  /// <exception cref="ArgumentOutOfRangeException">The bounds are not one of <see cref="CompositeKeyBounds"/>.</exception>
  internal static void CheckBounds(CompositeKeyBounds bounds)
  {
    if ((bounds & ~CompositeKeyBounds.Exclusive) != 0)
      throw new ArgumentOutOfRangeException(nameof(bounds), bounds, "The bounds include or exclude each of their two values.");
  }

  /// <summary>Bytes followed by 0xFF, which no element starts with, so every key that starts with the bytes sorts before it.</summary>
  internal static byte[] UpperBound(ReadOnlySpan<byte> prefix)
  {
    var to = new byte[prefix.Length + 1];
    prefix.CopyTo(to);
    to[^1] = PrefixEnd;
    return to;
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

  /// <summary>
  /// The text of a key, for logs, debugging and tools: its elements in order, such as ("by-customer", "bob", 42),
  /// each in a form its type can be told from. It reads any bytes: those that are not a composite key show as
  /// malformed(0x...), from where they stop being one.
  /// </summary>
  /// <remarks>
  /// Strings are quoted with C# escapes, bytes are hex after 0x, integers are digits, a floating value always has a
  /// point or an exponent, a decimal ends with m, and a descending element is desc(...). A DateTime, a
  /// DateTimeOffset and a DateOnly are ISO 8601, a TimeOnly is ISO 8601 after its T designator, a TimeSpan has its
  /// constant format, [-][d.]hh:mm:ss[.fffffff], and a Guid its hyphenated form. The text is culture-invariant.
  /// </remarks>
  public static string Format(ReadOnlySpan<byte> key)
  {
    var text = new StringBuilder("(");
    var reader = new CompositeKeyReader(key);
    while (reader.TryNext())
    {
      if (reader.Index > 0)
        text.Append(", ");
      var element = reader.ElementBytes;
      var tag = AscendingTag(element[0], out var mask);
      var start = text.Length;
      if (mask != 0)
        text.Append("desc(");
      if (!CompositeElementText.TryAppend<CompositeKeyElementFormat>(
          text, element.Slice(1), new ElementInfo(TypeOf(tag), tag, mask, reader.Index)))
      {
        // A string of invalid UTF-8 is where the key stops being one.
        text.Length = start;
        CompositeElementText.AppendMalformed(text, key.Slice(reader.Offset));
        return text.Append(')').ToString();
      }
      if (mask != 0)
        text.Append(')');
    }
    if (!reader.AtEnd)
    {
      if (reader.Index >= 0)
        text.Append(", ");
      CompositeElementText.AppendMalformed(text, key.Slice(reader.Offset));
    }
    return text.Append(')').ToString();
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
