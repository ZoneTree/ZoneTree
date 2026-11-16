using ZoneTree.Exceptions;

namespace ZoneTree.Composite;

/// <summary>
/// The shape of one key space of composite keys: the constant elements its keys start with, such as the name of
/// an index, then an element of a fixed type and order at each position. Keys made through a layout always agree
/// on both, so the same value never becomes two keys, and a layout's ranges hold exactly its keys.
/// </summary>
/// <remarks>
/// A layout is made once, as a static definition, and is safe for concurrent use. Its typed forms,
/// <see cref="CompositeKeyLayout{T1}"/> to <see cref="CompositeKeyLayout{T1, T2, T3, T4, T5, T6, T7, T8}"/>,
/// create, decode and bound its keys. Their ranges are <see cref="CompositeKeyRange"/> values, as those of
/// <see cref="CompositeKey"/> are, and compare by value at every position, whichever its order: on a descending
/// position, AtLeast holds the greater values, which sort first.
/// </remarks>
public abstract class CompositeKeyLayout
{
  readonly byte[] prefix;
  readonly bool[] descending;
  // The number of the prefix's elements, from which a read of a key counts the elements after the prefix.
  readonly int prefixCount;

  private protected CompositeKeyLayout(ReadOnlySpan<byte> prefix, CompositeKeyOrder[] orders)
  {
    if (!CompositeKey.IsValid(prefix))
      throw new ArgumentException("A layout's prefix is composite key elements.", nameof(prefix));
    this.prefix = prefix.ToArray();
    prefixCount = CompositeKey.ElementCount(prefix);
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

  /// <summary>The range of every key of the layout.</summary>
  public CompositeKeyRange Range() => CompositeKey.RangeUnder(prefix.AsSpan().ToArray());

  /// <exception cref="NotSupportedException">Composite keys do not support the type, or it is a <see cref="Descending{T}"/>.</exception>
  private protected static void Check<T>()
  {
    var shape = ShapeOf<T>.Value;
    if (shape.Kind == ElementKind.Unsupported)
      throw new NotSupportedException($"A composite key element cannot be a {typeof(T)}.");
    if (shape.IsDescending)
      throw new NotSupportedException("A layout gives each position its order, so a position's type is the element's own type, not a Descending<T>.");
  }

  /// <summary>The range of the keys under a key of the layout, which keeps the key's array.</summary>
  private protected static CompositeKeyRange RangeUnder(byte[] key) => CompositeKey.RangeUnder(key);

  /// <summary>
  /// The range from whichever of two keys of the layout sorts first through the other, each included or excluded as
  /// the bounds say, which keeps an included first key's array.
  /// </summary>
  /// <exception cref="ArgumentOutOfRangeException">The bounds are not one of <see cref="CompositeKeyBounds"/>.</exception>
  private protected static CompositeKeyRange RangeBetween(byte[] low, byte[] high, CompositeKeyBounds bounds)
  {
    CompositeKey.CheckBounds(bounds);
    return CompositeKey.RangeBetween(low, high, bounds, low, high);
  }

  /// <summary>The range of the keys whose element after a key's leading elements is at least the key's last.</summary>
  private protected static CompositeKeyRange RangeAtLeast(byte[] key)
      => CompositeKey.OneSided(key, lowEnd: true, inclusive: true, key);

  /// <summary>The range of the keys whose element after a key's leading elements is greater than the key's last.</summary>
  private protected static CompositeKeyRange RangeGreaterThan(byte[] key)
      => CompositeKey.OneSided(key, lowEnd: true, inclusive: false);

  /// <summary>The range of the keys whose element after a key's leading elements is at most the key's last.</summary>
  private protected static CompositeKeyRange RangeAtMost(byte[] key)
      => CompositeKey.OneSided(key, lowEnd: false, inclusive: true, key);

  /// <summary>The range of the keys whose element after a key's leading elements is less than the key's last.</summary>
  private protected static CompositeKeyRange RangeLessThan(byte[] key)
      => CompositeKey.OneSided(key, lowEnd: false, inclusive: false);

  /// <summary>The range of the keys whose element at a position, after the elements before it, begins with text.</summary>
  /// <exception cref="ArgumentNullException">The text is null.</exception>
  /// <exception cref="ArgumentException">The text is not valid UTF-16.</exception>
  /// <exception cref="PositionTypeMismatchException">The position's type is not string.</exception>
  private protected CompositeKeyRange RangeBeginningWith<T>(ReadOnlySpan<byte> elements, int position, string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    if (ShapeOf<T>.Value.Kind != ElementKind.String)
      throw new PositionTypeMismatchException(position, typeof(T), CompositeElementType.String);
    return CompositeKey.BeginsWithText(elements, text, descending[position]);
  }

  /// <summary>The range of the keys whose element at a position, after the elements before it, begins with bytes.</summary>
  /// <exception cref="PositionTypeMismatchException">The position's type is not a bytes type.</exception>
  private protected CompositeKeyRange RangeBeginningWith<T>(
      ReadOnlySpan<byte> elements, int position, ReadOnlySpan<byte> bytes)
  {
    if (ShapeOf<T>.Value.Kind is not (ElementKind.ByteArray or ElementKind.ByteMemory or ElementKind.ReadOnlyByteMemory))
      throw new PositionTypeMismatchException(position, typeof(T), CompositeElementType.Bytes);
    return CompositeKey.BeginsWithBytes(elements, bytes, descending[position]);
  }

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
    return new CompositeKeyReader(key, prefix.Length, prefixCount);
  }

  /// <summary>Reads the element at a position after the prefix, checked to be there in the layout's order.</summary>
  /// <exception cref="ArgumentException">The key has no element there, or it has another order.</exception>
  private protected T Next<T>(ref CompositeKeyReader reader, int position)
  {
    if (!reader.Next())
      throw new ArgumentException($"The key has {position} elements after the layout's prefix, not {descending.Length}.");
    if (reader.IsDescending != descending[position])
      throw new ArgumentException(
          $"Element {reader.Index} sorts {(reader.IsDescending ? "descending" : "ascending")}, and the layout's sorts {(descending[position] ? "descending" : "ascending")}.");
    return reader.Read<T>();
  }

  /// <summary>Checks that a key has no element after the layout's.</summary>
  /// <exception cref="ArgumentException">The key has more elements.</exception>
  private protected void End(ref CompositeKeyReader reader)
  {
    if (reader.Next())
      throw new ArgumentException($"The key has more than the {descending.Length} elements of the layout after its prefix.");
  }

  /// <summary>A reader over a key's elements after the layout's prefix, unless the key does not start with it.</summary>
  private protected bool TryElements(ReadOnlySpan<byte> key, out CompositeKeyReader reader)
  {
    if (!Matches(key))
    {
      reader = default;
      return false;
    }
    reader = new CompositeKeyReader(key, prefix.Length, prefixCount);
    return true;
  }

  /// <summary>Reads the element at a position after the prefix, unless it is not there in the layout's order and type.</summary>
  private protected bool TryNext<T>(ref CompositeKeyReader reader, int position, out T value)
  {
    if (reader.TryNext() && reader.IsDescending == descending[position])
      return reader.TryRead(out value);
    value = default;
    return false;
  }

  /// <summary>Whether a key ends after the layout's elements.</summary>
  private protected static bool TryEnd(ref CompositeKeyReader reader) => !reader.TryNext() && reader.AtEnd;
}
