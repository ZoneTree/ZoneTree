using System.Diagnostics.CodeAnalysis;

namespace ZoneTree.Composite;

/// <summary>
/// The shape of one key space of composite keys: the constant elements its keys start with, such as the name of
/// an index, then an element of a fixed type and order at each position. Keys made through a layout always agree
/// on both, so the same value never becomes two keys, and a layout's ranges hold exactly its keys.
/// </summary>
/// <remarks>
/// A layout is made once, as a static definition, and is safe for concurrent use. Its typed forms,
/// <see cref="CompositeKeyLayout{T1}"/> to <see cref="CompositeKeyLayout{T1, T2, T3, T4, T5, T6, T7, T8}"/>,
/// create, decode and bound its keys.
/// </remarks>
public abstract class CompositeKeyLayout
{
  readonly byte[] prefix;
  readonly bool[] descending;

  private protected CompositeKeyLayout(ReadOnlySpan<byte> prefix, CompositeKeyOrder[] orders)
  {
    if (!CompositeKey.IsValid(prefix))
      throw new ArgumentException("A layout's prefix is composite key elements.", nameof(prefix));
    this.prefix = prefix.ToArray();
    descending = new bool[orders.Length];
    for (var i = 0; i < orders.Length; ++i)
      descending[i] = CompositeKey.Descends(orders[i]);
  }

  /// <summary>The encoded elements every key of the layout starts with.</summary>
  public ReadOnlySpan<byte> Prefix => prefix;

  /// <summary>The number of elements after the prefix.</summary>
  public int ElementCount => descending.Length;

  /// <summary>The order of an element after the prefix.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The layout has no element at the position.</exception>
  public CompositeKeyOrder Order(int position)
  {
    if ((uint)position >= (uint)descending.Length)
      throw new ArgumentOutOfRangeException(nameof(position), position, $"The layout has {descending.Length} elements after its prefix.");
    return descending[position] ? CompositeKeyOrder.Descending : CompositeKeyOrder.Ascending;
  }

  /// <summary>Whether a key starts with the layout's prefix, as every key of the layout does.</summary>
  public bool Matches(ReadOnlySpan<byte> key) => key.StartsWith(prefix);

  /// <summary>The inclusive bounds of every key of the layout.</summary>
  public (byte[] From, byte[] To) Range() => CompositeKey.Bounds(prefix.AsSpan().ToArray(), 1);

  /// <exception cref="NotSupportedException">Composite keys do not support the type, or it is a <see cref="Descending{T}"/>.</exception>
  private protected static void Check<T>()
  {
    var shape = ShapeOf<T>.Value;
    if (shape.Kind == ElementKind.Unsupported)
      throw new NotSupportedException($"A composite key element cannot be a {typeof(T)}.");
    if (shape.IsDescending)
      throw new NotSupportedException("A layout gives each position its order, so a position's type is the element's own type, not a Descending<T>.");
  }

  private protected static (byte[] From, byte[] To) Bounds(byte[] key) => CompositeKey.Bounds(key, 1);

  /// <summary>An array for a key of the prefix and elements of a measured length, with the prefix written.</summary>
  byte[] Start(int length)
  {
    var key = new byte[prefix.Length + length];
    prefix.CopyTo(key, 0);
    return key;
  }

  private protected byte[] Key<TA>(in TA a)
  {
    var size = new CompositeKeyWriter();
    size.Append(a, descending[0]);
    var key = Start(size.Length);
    var writer = new CompositeKeyWriter(key, prefix.Length);
    writer.Append(a, descending[0]);
    return key;
  }

  private protected byte[] Key<TA, TB>(in TA a, in TB b)
  {
    var size = new CompositeKeyWriter();
    size.Append(a, descending[0]);
    size.Append(b, descending[1]);
    var key = Start(size.Length);
    var writer = new CompositeKeyWriter(key, prefix.Length);
    writer.Append(a, descending[0]);
    writer.Append(b, descending[1]);
    return key;
  }

  private protected byte[] Key<TA, TB, TC>(in TA a, in TB b, in TC c)
  {
    var size = new CompositeKeyWriter();
    size.Append(a, descending[0]);
    size.Append(b, descending[1]);
    size.Append(c, descending[2]);
    var key = Start(size.Length);
    var writer = new CompositeKeyWriter(key, prefix.Length);
    writer.Append(a, descending[0]);
    writer.Append(b, descending[1]);
    writer.Append(c, descending[2]);
    return key;
  }

  private protected byte[] Key<TA, TB, TC, TD>(in TA a, in TB b, in TC c, in TD d)
  {
    var size = new CompositeKeyWriter();
    size.Append(a, descending[0]);
    size.Append(b, descending[1]);
    size.Append(c, descending[2]);
    size.Append(d, descending[3]);
    var key = Start(size.Length);
    var writer = new CompositeKeyWriter(key, prefix.Length);
    writer.Append(a, descending[0]);
    writer.Append(b, descending[1]);
    writer.Append(c, descending[2]);
    writer.Append(d, descending[3]);
    return key;
  }

  private protected byte[] Key<TA, TB, TC, TD, TE>(in TA a, in TB b, in TC c, in TD d, in TE e)
  {
    var size = new CompositeKeyWriter();
    size.Append(a, descending[0]);
    size.Append(b, descending[1]);
    size.Append(c, descending[2]);
    size.Append(d, descending[3]);
    size.Append(e, descending[4]);
    var key = Start(size.Length);
    var writer = new CompositeKeyWriter(key, prefix.Length);
    writer.Append(a, descending[0]);
    writer.Append(b, descending[1]);
    writer.Append(c, descending[2]);
    writer.Append(d, descending[3]);
    writer.Append(e, descending[4]);
    return key;
  }

  private protected byte[] Key<TA, TB, TC, TD, TE, TF>(in TA a, in TB b, in TC c, in TD d, in TE e, in TF f)
  {
    var size = new CompositeKeyWriter();
    size.Append(a, descending[0]);
    size.Append(b, descending[1]);
    size.Append(c, descending[2]);
    size.Append(d, descending[3]);
    size.Append(e, descending[4]);
    size.Append(f, descending[5]);
    var key = Start(size.Length);
    var writer = new CompositeKeyWriter(key, prefix.Length);
    writer.Append(a, descending[0]);
    writer.Append(b, descending[1]);
    writer.Append(c, descending[2]);
    writer.Append(d, descending[3]);
    writer.Append(e, descending[4]);
    writer.Append(f, descending[5]);
    return key;
  }

  private protected byte[] Key<TA, TB, TC, TD, TE, TF, TG>(in TA a, in TB b, in TC c, in TD d, in TE e, in TF f, in TG g)
  {
    var size = new CompositeKeyWriter();
    size.Append(a, descending[0]);
    size.Append(b, descending[1]);
    size.Append(c, descending[2]);
    size.Append(d, descending[3]);
    size.Append(e, descending[4]);
    size.Append(f, descending[5]);
    size.Append(g, descending[6]);
    var key = Start(size.Length);
    var writer = new CompositeKeyWriter(key, prefix.Length);
    writer.Append(a, descending[0]);
    writer.Append(b, descending[1]);
    writer.Append(c, descending[2]);
    writer.Append(d, descending[3]);
    writer.Append(e, descending[4]);
    writer.Append(f, descending[5]);
    writer.Append(g, descending[6]);
    return key;
  }

  private protected byte[] Key<TA, TB, TC, TD, TE, TF, TG, TH>(
      in TA a, in TB b, in TC c, in TD d, in TE e, in TF f, in TG g, in TH h)
  {
    var size = new CompositeKeyWriter();
    size.Append(a, descending[0]);
    size.Append(b, descending[1]);
    size.Append(c, descending[2]);
    size.Append(d, descending[3]);
    size.Append(e, descending[4]);
    size.Append(f, descending[5]);
    size.Append(g, descending[6]);
    size.Append(h, descending[7]);
    var key = Start(size.Length);
    var writer = new CompositeKeyWriter(key, prefix.Length);
    writer.Append(a, descending[0]);
    writer.Append(b, descending[1]);
    writer.Append(c, descending[2]);
    writer.Append(d, descending[3]);
    writer.Append(e, descending[4]);
    writer.Append(f, descending[5]);
    writer.Append(g, descending[6]);
    writer.Append(h, descending[7]);
    return key;
  }

  /// <summary>A reader over a key's elements after the layout's prefix.</summary>
  /// <exception cref="ArgumentException">The key does not start with the prefix.</exception>
  private protected CompositeKeyReader Elements(ReadOnlySpan<byte> key)
  {
    if (!Matches(key))
      throw new ArgumentException("The key does not start with the layout's prefix.", nameof(key));
    return new CompositeKeyReader(key.Slice(prefix.Length));
  }

  /// <summary>Reads the element at a position after the prefix, checked to be there in the layout's order.</summary>
  /// <exception cref="ArgumentException">The key has no element there, or it has another order.</exception>
  private protected T Next<T>(ref CompositeKeyReader reader, int position)
  {
    if (!reader.Next())
      throw new ArgumentException($"The key has {position} elements after the layout's prefix, not {descending.Length}.");
    if (reader.IsDescending != descending[position])
      throw new ArgumentException(
          $"Element {position} after the layout's prefix sorts {(reader.IsDescending ? "descending" : "ascending")}, and the layout's sorts {(descending[position] ? "descending" : "ascending")}.");
    return reader.Read<T>();
  }

  /// <summary>Checks that a key has no element after the layout's.</summary>
  /// <exception cref="ArgumentException">The key has more elements.</exception>
  private protected void End(ref CompositeKeyReader reader)
  {
    if (reader.Next())
      throw new ArgumentException($"The key has more than the {descending.Length} elements of the layout after its prefix.");
  }
}

/// <summary>A layout of composite keys of a prefix and one element.</summary>
/// <typeparam name="T1">The type of the element after the prefix.</typeparam>
public sealed class CompositeKeyLayout<T1> : CompositeKeyLayout
{
  /// <summary>A layout of keys that start with a prefix and go on with an element of the type, in the order given.</summary>
  /// <param name="prefix">The constant elements every key starts with, such as the name of an index; none for no prefix.</param>
  /// <param name="first">The order of the element after the prefix.</param>
  /// <exception cref="ArgumentException">The prefix is not composite key elements.</exception>
  /// <exception cref="ArgumentOutOfRangeException">An order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support the type, or it is a <see cref="Descending{T}"/>.</exception>
  public CompositeKeyLayout(ReadOnlySpan<byte> prefix, CompositeKeyOrder first = CompositeKeyOrder.Ascending)
      : base(prefix, [first])
  {
    Check<T1>();
  }

  /// <summary>Encodes a key of the layout.</summary>
  public byte[] Create(T1 first) => Key(first);

  /// <summary>Reads a key of the layout.</summary>
  /// <exception cref="ArgumentException">The key is not of the layout.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  public T1 Decode(ReadOnlySpan<byte> key)
  {
    var reader = Elements(key);
    var first = Next<T1>(ref reader, 0);
    End(ref reader);
    return first;
  }
}

/// <summary>A layout of composite keys of a prefix and two elements.</summary>
public sealed class CompositeKeyLayout<T1, T2> : CompositeKeyLayout
{
  /// <summary>A layout of keys that start with a prefix and go on with an element of each type, in the orders given.</summary>
  /// <exception cref="ArgumentException">The prefix is not composite key elements.</exception>
  /// <exception cref="ArgumentOutOfRangeException">An order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type, or it is a <see cref="Descending{T}"/>.</exception>
  public CompositeKeyLayout(ReadOnlySpan<byte> prefix,
      CompositeKeyOrder first = CompositeKeyOrder.Ascending, CompositeKeyOrder second = CompositeKeyOrder.Ascending)
      : base(prefix, [first, second])
  {
    Check<T1>();
    Check<T2>();
  }

  /// <summary>Encodes a key of the layout.</summary>
  public byte[] Create(T1 first, T2 second) => Key(first, second);

  /// <summary>The inclusive bounds of the layout's keys that start with an element.</summary>
  public (byte[] From, byte[] To) Range(T1 first) => Bounds(Key(first));

  /// <summary>Reads a key of the layout.</summary>
  /// <exception cref="ArgumentException">The key is not of the layout.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  public (T1, T2) Decode(ReadOnlySpan<byte> key)
  {
    var reader = Elements(key);
    var elements = (Next<T1>(ref reader, 0), Next<T2>(ref reader, 1));
    End(ref reader);
    return elements;
  }
}

/// <summary>A layout of composite keys of a prefix and three elements.</summary>
[SuppressMessage("Design", "CA1005", Justification = "A layout has a type for each element, as a tuple does.")]
public sealed class CompositeKeyLayout<T1, T2, T3> : CompositeKeyLayout
{
  /// <summary>A layout of keys that start with a prefix and go on with an element of each type, in the orders given.</summary>
  /// <exception cref="ArgumentException">The prefix is not composite key elements.</exception>
  /// <exception cref="ArgumentOutOfRangeException">An order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type, or it is a <see cref="Descending{T}"/>.</exception>
  public CompositeKeyLayout(ReadOnlySpan<byte> prefix,
      CompositeKeyOrder first = CompositeKeyOrder.Ascending, CompositeKeyOrder second = CompositeKeyOrder.Ascending,
      CompositeKeyOrder third = CompositeKeyOrder.Ascending)
      : base(prefix, [first, second, third])
  {
    Check<T1>();
    Check<T2>();
    Check<T3>();
  }

  /// <summary>Encodes a key of the layout.</summary>
  public byte[] Create(T1 first, T2 second, T3 third) => Key(first, second, third);

  /// <summary>The inclusive bounds of the layout's keys that start with an element.</summary>
  public (byte[] From, byte[] To) Range(T1 first) => Bounds(Key(first));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second) => Bounds(Key(first, second));

  /// <summary>Reads a key of the layout.</summary>
  /// <exception cref="ArgumentException">The key is not of the layout.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  public (T1, T2, T3) Decode(ReadOnlySpan<byte> key)
  {
    var reader = Elements(key);
    var elements = (Next<T1>(ref reader, 0), Next<T2>(ref reader, 1), Next<T3>(ref reader, 2));
    End(ref reader);
    return elements;
  }
}

/// <summary>A layout of composite keys of a prefix and four elements.</summary>
[SuppressMessage("Design", "CA1005", Justification = "A layout has a type for each element, as a tuple does.")]
public sealed class CompositeKeyLayout<T1, T2, T3, T4> : CompositeKeyLayout
{
  /// <summary>A layout of keys that start with a prefix and go on with an element of each type, in the orders given.</summary>
  /// <exception cref="ArgumentException">The prefix is not composite key elements.</exception>
  /// <exception cref="ArgumentOutOfRangeException">An order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type, or it is a <see cref="Descending{T}"/>.</exception>
  public CompositeKeyLayout(ReadOnlySpan<byte> prefix,
      CompositeKeyOrder first = CompositeKeyOrder.Ascending, CompositeKeyOrder second = CompositeKeyOrder.Ascending,
      CompositeKeyOrder third = CompositeKeyOrder.Ascending, CompositeKeyOrder fourth = CompositeKeyOrder.Ascending)
      : base(prefix, [first, second, third, fourth])
  {
    Check<T1>();
    Check<T2>();
    Check<T3>();
    Check<T4>();
  }

  /// <summary>Encodes a key of the layout.</summary>
  public byte[] Create(T1 first, T2 second, T3 third, T4 fourth) => Key(first, second, third, fourth);

  /// <summary>The inclusive bounds of the layout's keys that start with an element.</summary>
  public (byte[] From, byte[] To) Range(T1 first) => Bounds(Key(first));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second) => Bounds(Key(first, second));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third) => Bounds(Key(first, second, third));

  /// <summary>Reads a key of the layout.</summary>
  /// <exception cref="ArgumentException">The key is not of the layout.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  public (T1, T2, T3, T4) Decode(ReadOnlySpan<byte> key)
  {
    var reader = Elements(key);
    var elements = (Next<T1>(ref reader, 0), Next<T2>(ref reader, 1), Next<T3>(ref reader, 2), Next<T4>(ref reader, 3));
    End(ref reader);
    return elements;
  }
}

/// <summary>A layout of composite keys of a prefix and five elements.</summary>
[SuppressMessage("Design", "CA1005", Justification = "A layout has a type for each element, as a tuple does.")]
public sealed class CompositeKeyLayout<T1, T2, T3, T4, T5> : CompositeKeyLayout
{
  /// <summary>A layout of keys that start with a prefix and go on with an element of each type, in the orders given.</summary>
  /// <exception cref="ArgumentException">The prefix is not composite key elements.</exception>
  /// <exception cref="ArgumentOutOfRangeException">An order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type, or it is a <see cref="Descending{T}"/>.</exception>
  public CompositeKeyLayout(ReadOnlySpan<byte> prefix,
      CompositeKeyOrder first = CompositeKeyOrder.Ascending, CompositeKeyOrder second = CompositeKeyOrder.Ascending,
      CompositeKeyOrder third = CompositeKeyOrder.Ascending, CompositeKeyOrder fourth = CompositeKeyOrder.Ascending,
      CompositeKeyOrder fifth = CompositeKeyOrder.Ascending)
      : base(prefix, [first, second, third, fourth, fifth])
  {
    Check<T1>();
    Check<T2>();
    Check<T3>();
    Check<T4>();
    Check<T5>();
  }

  /// <summary>Encodes a key of the layout.</summary>
  public byte[] Create(T1 first, T2 second, T3 third, T4 fourth, T5 fifth) => Key(first, second, third, fourth, fifth);

  /// <summary>The inclusive bounds of the layout's keys that start with an element.</summary>
  public (byte[] From, byte[] To) Range(T1 first) => Bounds(Key(first));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second) => Bounds(Key(first, second));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third) => Bounds(Key(first, second, third));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third, T4 fourth) => Bounds(Key(first, second, third, fourth));

  /// <summary>Reads a key of the layout.</summary>
  /// <exception cref="ArgumentException">The key is not of the layout.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  public (T1, T2, T3, T4, T5) Decode(ReadOnlySpan<byte> key)
  {
    var reader = Elements(key);
    var elements = (Next<T1>(ref reader, 0), Next<T2>(ref reader, 1), Next<T3>(ref reader, 2), Next<T4>(ref reader, 3),
        Next<T5>(ref reader, 4));
    End(ref reader);
    return elements;
  }
}

/// <summary>A layout of composite keys of a prefix and six elements.</summary>
[SuppressMessage("Design", "CA1005", Justification = "A layout has a type for each element, as a tuple does.")]
public sealed class CompositeKeyLayout<T1, T2, T3, T4, T5, T6> : CompositeKeyLayout
{
  /// <summary>A layout of keys that start with a prefix and go on with an element of each type, in the orders given.</summary>
  /// <exception cref="ArgumentException">The prefix is not composite key elements.</exception>
  /// <exception cref="ArgumentOutOfRangeException">An order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type, or it is a <see cref="Descending{T}"/>.</exception>
  public CompositeKeyLayout(ReadOnlySpan<byte> prefix,
      CompositeKeyOrder first = CompositeKeyOrder.Ascending, CompositeKeyOrder second = CompositeKeyOrder.Ascending,
      CompositeKeyOrder third = CompositeKeyOrder.Ascending, CompositeKeyOrder fourth = CompositeKeyOrder.Ascending,
      CompositeKeyOrder fifth = CompositeKeyOrder.Ascending, CompositeKeyOrder sixth = CompositeKeyOrder.Ascending)
      : base(prefix, [first, second, third, fourth, fifth, sixth])
  {
    Check<T1>();
    Check<T2>();
    Check<T3>();
    Check<T4>();
    Check<T5>();
    Check<T6>();
  }

  /// <summary>Encodes a key of the layout.</summary>
  public byte[] Create(T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth)
      => Key(first, second, third, fourth, fifth, sixth);

  /// <summary>The inclusive bounds of the layout's keys that start with an element.</summary>
  public (byte[] From, byte[] To) Range(T1 first) => Bounds(Key(first));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second) => Bounds(Key(first, second));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third) => Bounds(Key(first, second, third));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third, T4 fourth) => Bounds(Key(first, second, third, fourth));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third, T4 fourth, T5 fifth)
      => Bounds(Key(first, second, third, fourth, fifth));

  /// <summary>Reads a key of the layout.</summary>
  /// <exception cref="ArgumentException">The key is not of the layout.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  public (T1, T2, T3, T4, T5, T6) Decode(ReadOnlySpan<byte> key)
  {
    var reader = Elements(key);
    var elements = (Next<T1>(ref reader, 0), Next<T2>(ref reader, 1), Next<T3>(ref reader, 2), Next<T4>(ref reader, 3),
        Next<T5>(ref reader, 4), Next<T6>(ref reader, 5));
    End(ref reader);
    return elements;
  }
}

/// <summary>A layout of composite keys of a prefix and seven elements.</summary>
[SuppressMessage("Design", "CA1005", Justification = "A layout has a type for each element, as a tuple does.")]
public sealed class CompositeKeyLayout<T1, T2, T3, T4, T5, T6, T7> : CompositeKeyLayout
{
  /// <summary>A layout of keys that start with a prefix and go on with an element of each type, in the orders given.</summary>
  /// <exception cref="ArgumentException">The prefix is not composite key elements.</exception>
  /// <exception cref="ArgumentOutOfRangeException">An order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type, or it is a <see cref="Descending{T}"/>.</exception>
  public CompositeKeyLayout(ReadOnlySpan<byte> prefix,
      CompositeKeyOrder first = CompositeKeyOrder.Ascending, CompositeKeyOrder second = CompositeKeyOrder.Ascending,
      CompositeKeyOrder third = CompositeKeyOrder.Ascending, CompositeKeyOrder fourth = CompositeKeyOrder.Ascending,
      CompositeKeyOrder fifth = CompositeKeyOrder.Ascending, CompositeKeyOrder sixth = CompositeKeyOrder.Ascending,
      CompositeKeyOrder seventh = CompositeKeyOrder.Ascending)
      : base(prefix, [first, second, third, fourth, fifth, sixth, seventh])
  {
    Check<T1>();
    Check<T2>();
    Check<T3>();
    Check<T4>();
    Check<T5>();
    Check<T6>();
    Check<T7>();
  }

  /// <summary>Encodes a key of the layout.</summary>
  public byte[] Create(T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth, T7 seventh)
      => Key(first, second, third, fourth, fifth, sixth, seventh);

  /// <summary>The inclusive bounds of the layout's keys that start with an element.</summary>
  public (byte[] From, byte[] To) Range(T1 first) => Bounds(Key(first));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second) => Bounds(Key(first, second));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third) => Bounds(Key(first, second, third));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third, T4 fourth) => Bounds(Key(first, second, third, fourth));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third, T4 fourth, T5 fifth)
      => Bounds(Key(first, second, third, fourth, fifth));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth)
      => Bounds(Key(first, second, third, fourth, fifth, sixth));

  /// <summary>Reads a key of the layout.</summary>
  /// <exception cref="ArgumentException">The key is not of the layout.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  public (T1, T2, T3, T4, T5, T6, T7) Decode(ReadOnlySpan<byte> key)
  {
    var reader = Elements(key);
    var elements = (Next<T1>(ref reader, 0), Next<T2>(ref reader, 1), Next<T3>(ref reader, 2), Next<T4>(ref reader, 3),
        Next<T5>(ref reader, 4), Next<T6>(ref reader, 5), Next<T7>(ref reader, 6));
    End(ref reader);
    return elements;
  }
}

/// <summary>A layout of composite keys of a prefix and eight elements.</summary>
[SuppressMessage("Design", "CA1005", Justification = "A layout has a type for each element, as a tuple does.")]
public sealed class CompositeKeyLayout<T1, T2, T3, T4, T5, T6, T7, T8> : CompositeKeyLayout
{
  /// <summary>A layout of keys that start with a prefix and go on with an element of each type, in the orders given.</summary>
  /// <exception cref="ArgumentException">The prefix is not composite key elements.</exception>
  /// <exception cref="ArgumentOutOfRangeException">An order is not one of <see cref="CompositeKeyOrder"/>.</exception>
  /// <exception cref="NotSupportedException">Composite keys do not support a type, or it is a <see cref="Descending{T}"/>.</exception>
  public CompositeKeyLayout(ReadOnlySpan<byte> prefix,
      CompositeKeyOrder first = CompositeKeyOrder.Ascending, CompositeKeyOrder second = CompositeKeyOrder.Ascending,
      CompositeKeyOrder third = CompositeKeyOrder.Ascending, CompositeKeyOrder fourth = CompositeKeyOrder.Ascending,
      CompositeKeyOrder fifth = CompositeKeyOrder.Ascending, CompositeKeyOrder sixth = CompositeKeyOrder.Ascending,
      CompositeKeyOrder seventh = CompositeKeyOrder.Ascending, CompositeKeyOrder eighth = CompositeKeyOrder.Ascending)
      : base(prefix, [first, second, third, fourth, fifth, sixth, seventh, eighth])
  {
    Check<T1>();
    Check<T2>();
    Check<T3>();
    Check<T4>();
    Check<T5>();
    Check<T6>();
    Check<T7>();
    Check<T8>();
  }

  /// <summary>Encodes a key of the layout.</summary>
  public byte[] Create(T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth, T7 seventh, T8 eighth)
      => Key(first, second, third, fourth, fifth, sixth, seventh, eighth);

  /// <summary>The inclusive bounds of the layout's keys that start with an element.</summary>
  public (byte[] From, byte[] To) Range(T1 first) => Bounds(Key(first));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second) => Bounds(Key(first, second));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third) => Bounds(Key(first, second, third));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third, T4 fourth) => Bounds(Key(first, second, third, fourth));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third, T4 fourth, T5 fifth)
      => Bounds(Key(first, second, third, fourth, fifth));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth)
      => Bounds(Key(first, second, third, fourth, fifth, sixth));

  /// <summary>The inclusive bounds of the layout's keys that start with elements.</summary>
  public (byte[] From, byte[] To) Range(T1 first, T2 second, T3 third, T4 fourth, T5 fifth, T6 sixth, T7 seventh)
      => Bounds(Key(first, second, third, fourth, fifth, sixth, seventh));

  /// <summary>Reads a key of the layout.</summary>
  /// <exception cref="ArgumentException">The key is not of the layout.</exception>
  /// <exception cref="InvalidCastException">An element has another type.</exception>
  /// <exception cref="OverflowException">A type cannot hold its element's value exactly.</exception>
  public (T1, T2, T3, T4, T5, T6, T7, T8) Decode(ReadOnlySpan<byte> key)
  {
    var reader = Elements(key);
    var elements = (Next<T1>(ref reader, 0), Next<T2>(ref reader, 1), Next<T3>(ref reader, 2), Next<T4>(ref reader, 3),
        Next<T5>(ref reader, 4), Next<T6>(ref reader, 5), Next<T7>(ref reader, 6), Next<T8>(ref reader, 7));
    End(ref reader);
    return elements;
  }
}
