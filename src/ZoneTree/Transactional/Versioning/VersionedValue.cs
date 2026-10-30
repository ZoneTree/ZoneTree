namespace ZoneTree.Transactional.Versioning;

/// <summary>
/// The kinds of values in a transactional tree.
/// </summary>
public enum VersionedValueKind : byte
{
  /// <summary>
  /// A user value.
  /// </summary>
  Value = 0,

  /// <summary>
  /// A transactional delete of the user key.
  /// </summary>
  Tombstone = 1,

  /// <summary>
  /// A record removed from the storage tree, such as a version of a rolled back
  /// transaction. The storage tree treats it as deleted.
  /// </summary>
  Deleted = 2,

  /// <summary>
  /// A system record that keeps its payload in <see cref="VersionedValue{TValue}.Meta"/>.
  /// </summary>
  System = 3,
}

/// <summary>
/// The value of the tree that stores a transactional tree.
/// </summary>
/// <typeparam name="TValue">The user value type.</typeparam>
public readonly struct VersionedValue<TValue> : IEquatable<VersionedValue<TValue>>
{
  // The field order keeps the struct small: an unmanaged value is stored
  // with the fixed size of the struct.

  /// <summary>
  /// The payload of a system record.
  /// </summary>
  public readonly long Meta;

  /// <summary>
  /// The user value.
  /// </summary>
  public readonly TValue Value;

  /// <summary>
  /// The value kind.
  /// </summary>
  public readonly VersionedValueKind Kind;

  /// <summary>
  /// Creates a versioned value.
  /// </summary>
  public VersionedValue(VersionedValueKind kind, long meta, in TValue value)
  {
    Kind = kind;
    Meta = meta;
    Value = value;
  }

  /// <summary>
  /// Creates a user value.
  /// </summary>
  public static VersionedValue<TValue> FromValue(in TValue value)
      => new(VersionedValueKind.Value, 0, in value);

  /// <summary>
  /// Creates a system record value.
  /// </summary>
  public static VersionedValue<TValue> FromSystem(long meta)
      => new(VersionedValueKind.System, meta, default);

  /// <summary>
  /// The transactional delete marker.
  /// </summary>
  public static VersionedValue<TValue> Tombstone
      => new(VersionedValueKind.Tombstone, 0, default);

  /// <summary>
  /// The storage deletion marker.
  /// </summary>
  public static VersionedValue<TValue> Deleted
      => new(VersionedValueKind.Deleted, 0, default);

  public bool Equals(VersionedValue<TValue> other)
  {
    return Kind == other.Kind &&
        Meta == other.Meta &&
        EqualityComparer<TValue>.Default.Equals(Value, other.Value);
  }

  public override bool Equals(object obj)
  {
    return obj is VersionedValue<TValue> other && Equals(other);
  }

  public override int GetHashCode()
  {
    return HashCode.Combine(Kind, Meta, Value);
  }

  public static bool operator ==(VersionedValue<TValue> left, VersionedValue<TValue> right)
  {
    return left.Equals(right);
  }

  public static bool operator !=(VersionedValue<TValue> left, VersionedValue<TValue> right)
  {
    return !(left == right);
  }
}
