namespace ZoneTree.Composite;

/// <summary>
/// A range of composite keys, in byte order: the keys from <see cref="Lower"/>, which the range can hold, up to
/// <see cref="Upper"/>, which is never a key itself.
/// </summary>
/// <remarks>
/// A scan seeks one bound and reads while <see cref="Contains"/> holds: a forward iterator seeks
/// <see cref="Lower"/>, and a reverse iterator <see cref="Upper"/>. On a tree with the descending byte comparer,
/// each iterator walks the other way and seeks the other bound.
/// </remarks>
public readonly struct CompositeKeyRange : IEquatable<CompositeKeyRange>
{
  /// <summary>How many leading bytes the bounds have in common, which <see cref="Contains"/> reads once.</summary>
  readonly int shared;

  /// <summary>A range from a lower bound up to an upper bound, in byte order.</summary>
  /// <remarks>The range keeps both arrays, as a tree keeps its keys, so change neither afterwards.</remarks>
  /// <exception cref="ArgumentNullException">A bound is null.</exception>
  public CompositeKeyRange(byte[] lower, byte[] upper)
  {
    ArgumentNullException.ThrowIfNull(lower);
    ArgumentNullException.ThrowIfNull(upper);
    Lower = lower;
    Upper = upper;
#if NET7_0_OR_GREATER
    shared = lower.AsSpan().CommonPrefixLength(upper);
#else
    var length = Math.Min(lower.Length, upper.Length);
    while (shared < length && lower[shared] == upper[shared])
      ++shared;
#endif
  }

  /// <summary>The lowest bytes of the range, where a forward scan starts.</summary>
  public byte[] Lower { get; }

  /// <summary>The bytes just above the range, where a reverse scan starts.</summary>
  public byte[] Upper { get; }

  /// <summary>Whether a key is in the range: at or above <see cref="Lower"/>, and below <see cref="Upper"/>.</summary>
  /// <remarks>
  /// The bytes both bounds start with are read once: a key that differs within them sorts below both bounds or above
  /// both, so only a key that starts with them is compared further, and then only its rest with each bound's rest.
  /// </remarks>
  public bool Contains(ReadOnlySpan<byte> key)
  {
    var lower = Lower.AsSpan();
    if (!key.StartsWith(lower.Slice(0, shared)))
      return false;
    var rest = key.Slice(shared);
    return rest.SequenceCompareTo(lower.Slice(shared)) >= 0 && rest.SequenceCompareTo(Upper.AsSpan(shared)) < 0;
  }

  /// <summary>
  /// The keys of the range that sort after a key, such as the highest key one page read, for the page above it.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A forward iterator reads pages upward, so its next page starts after the last key it read. On a tree with the
  /// descending byte comparer, a reverse iterator reads upward instead.
  /// </para>
  /// <para>
  /// The key is one exact key, as a cursor is: keys that extend it sort after it, and stay in the range.
  /// <see cref="CompositeKey.GreaterThan"/> takes an end instead, which stands for every key under it.
  /// </para>
  /// </remarks>
  public CompositeKeyRange StartAfter(ReadOnlySpan<byte> key)
  {
    // The key followed by 0x00 sorts just after it: any bytes above the key are at or above these.
    var lower = new byte[key.Length + 1];
    key.CopyTo(lower);
    return lower.AsSpan().SequenceCompareTo(Lower) > 0 ? new(lower, Upper) : this;
  }

  /// <summary>
  /// The keys of the range that sort before a key, such as the lowest key one page read, for the page below it.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A reverse iterator reads pages downward, so its next page ends before the last key it read. On a tree with the
  /// descending byte comparer, a forward iterator reads downward instead.
  /// </para>
  /// <para>
  /// The key is one exact key, as a cursor is: keys that extend it sort after it, and are out of the range, and
  /// keys it extends sort before it, and stay in it.
  /// </para>
  /// </remarks>
  /// <exception cref="ArgumentException">The key's bytes are not a composite key.</exception>
  public CompositeKeyRange EndBefore(ReadOnlySpan<byte> key)
  {
    // No key sorts below the empty key; below any other, the bytes just under its elements have no key between.
    if (key.IsEmpty)
      return CompositeKey.NoKeys();
    var upper = CompositeKeyFormat.Below(key);
    return upper.AsSpan().SequenceCompareTo(Upper) < 0 ? new(Lower, upper) : this;
  }

  /// <summary>The bounds, as a tuple's elements.</summary>
  public void Deconstruct(out byte[] lower, out byte[] upper)
  {
    lower = Lower;
    upper = Upper;
  }

  /// <summary>Whether two ranges have the same bounds, byte for byte.</summary>
  public bool Equals(CompositeKeyRange other)
      => Lower.AsSpan().SequenceEqual(other.Lower) && Upper.AsSpan().SequenceEqual(other.Upper);

  public override bool Equals(object obj) => obj is CompositeKeyRange other && Equals(other);

  public override int GetHashCode()
  {
    var hash = new HashCode();
    hash.AddBytes(Lower);
    hash.AddBytes(Upper);
    return hash.ToHashCode();
  }

  /// <summary>The bounds in hex, as [lower, upper).</summary>
  public override string ToString() => $"[0x{Convert.ToHexString(Lower.AsSpan())}, 0x{Convert.ToHexString(Upper.AsSpan())})";

  public static bool operator ==(CompositeKeyRange left, CompositeKeyRange right) => left.Equals(right);

  public static bool operator !=(CompositeKeyRange left, CompositeKeyRange right) => !left.Equals(right);
}
