namespace ZoneTree.Transactional;

/// <summary>
/// The kinds of records in a transactional tree, in their storage order.
/// </summary>
public enum VersionedKeyKind : byte
{
  /// <summary>
  /// The transaction clock reservation. There is a single record of this kind.
  /// </summary>
  Clock = 0,

  /// <summary>
  /// The status of a transaction that wrote records and has not committed.
  /// The version is the transaction id.
  /// </summary>
  TransactionStatus = 1,

  /// <summary>
  /// A version of a user key. The version is the id of the writing transaction.
  /// </summary>
  Data = 2,
}

/// <summary>
/// The key of the tree that stores a transactional tree:
/// a user key with the id of the transaction that wrote it.
/// </summary>
/// <remarks>
/// Records sort by kind, then by user key, then by version in descending order,
/// so the newest version of a user key comes first.
/// Only <see cref="VersionedKeyKind.Data"/> records carry a user key.
/// </remarks>
/// <typeparam name="TKey">The user key type.</typeparam>
public readonly struct VersionedKey<TKey> : IEquatable<VersionedKey<TKey>>
{
  // The field order keeps the struct small: an unmanaged key is stored
  // with the fixed size of the struct.

  /// <summary>
  /// The transaction id of a data or status record.
  /// </summary>
  public readonly long Version;

  /// <summary>
  /// The user key of a data record.
  /// </summary>
  public readonly TKey Key;

  /// <summary>
  /// The record kind.
  /// </summary>
  public readonly VersionedKeyKind Kind;

  /// <summary>
  /// Creates a versioned key.
  /// </summary>
  public VersionedKey(VersionedKeyKind kind, in TKey key, long version)
  {
    Kind = kind;
    Key = key;
    Version = version;
  }

  /// <summary>
  /// Creates the key of a user key version.
  /// </summary>
  public static VersionedKey<TKey> Data(in TKey key, long version)
      => new(VersionedKeyKind.Data, in key, version);

  /// <summary>
  /// Creates the key of a transaction status record.
  /// </summary>
  public static VersionedKey<TKey> TransactionStatus(long transactionId)
      => new(VersionedKeyKind.TransactionStatus, default, transactionId);

  /// <summary>
  /// The key of the clock reservation record.
  /// </summary>
  public static VersionedKey<TKey> Clock => new(VersionedKeyKind.Clock, default, 0);

  public bool Equals(VersionedKey<TKey> other)
  {
    return Kind == other.Kind &&
        Version == other.Version &&
        EqualityComparer<TKey>.Default.Equals(Key, other.Key);
  }

  public override bool Equals(object obj)
  {
    return obj is VersionedKey<TKey> other && Equals(other);
  }

  public override int GetHashCode()
  {
    return HashCode.Combine(Kind, Key, Version);
  }

  public static bool operator ==(VersionedKey<TKey> left, VersionedKey<TKey> right)
  {
    return left.Equals(right);
  }

  public static bool operator !=(VersionedKey<TKey> left, VersionedKey<TKey> right)
  {
    return !(left == right);
  }
}
