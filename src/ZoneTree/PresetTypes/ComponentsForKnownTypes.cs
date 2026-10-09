using ZoneTree.Comparers;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.Hashers;

namespace ZoneTree.PresetTypes;

/// <summary>
/// Provides utility methods for handling known types, including obtaining comparers, serializers,
/// and determining if values are deleted.
/// </summary>
public static class ComponentsForKnownTypes
{
  /// <summary>
  /// Returns a comparer for a given type key. The comparer is used to compare instances
  /// of the type in ascending order.
  /// </summary>
  /// <typeparam name="TKey">The type of the key to compare.</typeparam>
  /// <returns>An implementation of <see cref="IRefComparer{TKey}"/> appropriate for the type, or null if unsupported.</returns>
  public static IRefComparer<TKey> GetComparer<TKey>()
  {
    TKey key = default;
    var comparer = key switch
    {
      byte => new ByteComparerAscending() as IRefComparer<TKey>,
      char => new CharComparerAscending() as IRefComparer<TKey>,
      DateTime => new DateTimeComparerAscending() as IRefComparer<TKey>,
      decimal => new DecimalComparerAscending() as IRefComparer<TKey>,
      double => new DoubleComparerAscending() as IRefComparer<TKey>,
      short => new Int16ComparerAscending() as IRefComparer<TKey>,
      ushort => new UInt16ComparerAscending() as IRefComparer<TKey>,
      int => new Int32ComparerAscending() as IRefComparer<TKey>,
      uint => new UInt32ComparerAscending() as IRefComparer<TKey>,
      long => new Int64ComparerAscending() as IRefComparer<TKey>,
      ulong => new UInt64ComparerAscending() as IRefComparer<TKey>,
      Guid => new GuidComparerAscending() as IRefComparer<TKey>,
      _ => null
    };
    if (typeof(TKey) == typeof(string))
      comparer =
          new StringOrdinalComparerAscending() as IRefComparer<TKey>;

    else if (typeof(TKey) == typeof(Memory<byte>))
      comparer =
          new ByteArrayLexicographicComparerAscending() as IRefComparer<TKey>;
    else if (typeof(TKey) == typeof(byte[]))
    {
      throw new ZoneTreeException("ZoneTree<byte[], ...> is not supported. Use ZoneTree<Memory<byte>, ...> instead.");
    }
    return comparer;
  }

  /// <summary>
  /// Returns a key hasher for a known key type.
  /// </summary>
  /// <typeparam name="TKey">The key type.</typeparam>
  /// <returns>
  /// An implementation of <see cref="IKeyHasher{TKey}"/> appropriate for the
  /// type, or null if the type is unsupported.
  /// </returns>
  public static IKeyHasher<TKey> GetKeyHasher<TKey>()
  {
    TKey key = default;
    var isKnownType = key is byte or char or DateTime or decimal or double or
        short or ushort or int or uint or long or ulong or Guid;

    if (isKnownType || typeof(TKey) == typeof(string))
      return new DefaultKeyHasher<TKey>();

    if (typeof(TKey) == typeof(Memory<byte>))
      return new ByteArrayKeyHasher() as IKeyHasher<TKey>;

    if (typeof(TKey) == typeof(byte[]))
    {
      throw new ZoneTreeException(
          "ZoneTree<byte[], ...> is not supported. " +
          "Use ZoneTree<Memory<byte>, ...> instead.");
    }
    return null;
  }

  /// <summary>
  /// Returns a serializer for a given type. The serializer is used to serialize and deserialize
  /// instances of the type.
  /// </summary>
  /// <typeparam name="T">The type to be serialized.</typeparam>
  /// <returns>An implementation of <see cref="ISerializer{T}"/> appropriate for the type, or null if unsupported.</returns>
  public static ISerializer<T> GetSerializer<T>()
  {
    T key = default;
    var serializer = key switch
    {
      byte => new ByteSerializer() as ISerializer<T>,
      bool => new BooleanSerializer() as ISerializer<T>,
      char => new CharSerializer() as ISerializer<T>,
      DateTime => new DateTimeSerializer() as ISerializer<T>,
      decimal => new DecimalSerializer() as ISerializer<T>,
      double => new DoubleSerializer() as ISerializer<T>,
      short => new Int16Serializer() as ISerializer<T>,
      ushort => new UInt16Serializer() as ISerializer<T>,
      int => new Int32Serializer() as ISerializer<T>,
      uint => new UInt32Serializer() as ISerializer<T>,
      long => new Int64Serializer() as ISerializer<T>,
      ulong => new UInt64Serializer() as ISerializer<T>,
      Guid => new StructSerializer<Guid>() as ISerializer<T>,
      _ => null
    };

    if (typeof(T) == typeof(string))
      serializer =
          new Utf8StringSerializer() as ISerializer<T>;
    else if (typeof(T) == typeof(Memory<byte>))
    {
      serializer =
          new ByteArraySerializer() as ISerializer<T>;
    }
    else if (typeof(T) == typeof(byte[]))
    {
      throw new ZoneTreeException("ZoneTree<byte[], ...> is not supported. Use ZoneTree<Memory<byte>, ...> instead.");
    }
    return serializer;
  }

  // Specific methods for checking if certain primitive types are considered deleted
  static bool IsDeletedByte<TKey>(in TKey key, in byte value) => value == default;
  static bool IsDeletedChar<TKey>(in TKey key, in char value) => value == default;
  static bool IsDeletedDateTime<TKey>(in TKey key, in DateTime value) => value == default;
  static bool IsDeletedDecimal<TKey>(in TKey key, in decimal value) => value == default;
  static bool IsDeletedDouble<TKey>(in TKey key, in double value) => value == default;
  static bool IsDeletedShort<TKey>(in TKey key, in short value) => value == default;
  static bool IsDeletedUShort<TKey>(in TKey key, in ushort value) => value == default;
  static bool IsDeletedInt<TKey>(in TKey key, in int value) => value == default;
  static bool IsDeletedUInt<TKey>(in TKey key, in uint value) => value == default;
  static bool IsDeletedLong<TKey>(in TKey key, in long value) => value == default;
  static bool IsDeletedULong<TKey>(in TKey key, in ulong value) => value == default;
  static bool IsDeletedGuid<TKey>(in TKey key, in Guid value) => value == default;
  static bool IsDeletedMemoryByte<TKey>(in TKey key, in Memory<byte> value) => value.Length == 0;
  static bool IsDeletedReferenceType<TKey, TValue>(in TKey key, in TValue value) => ReferenceEquals(value, default(TValue));
  static bool IsDeletedDefault<TKey, TValue>(in TKey key, in TValue value) => EqualityComparer<TValue>.Default.Equals(value, default);

  static void MarkValueDeletedDefault<TValue>(ref TValue value) { value = default; }

  /// <summary>
  /// Returns a delegate that checks if a key-value pair of a specific type is considered deleted.
  /// </summary>
  /// <typeparam name="TKey">The type of the key.</typeparam>
  /// <typeparam name="TValue">The type of the value.</typeparam>
  /// <returns>A delegate that checks if the value is considered deleted.</returns>
  public static IsDeletedDelegate<TKey, TValue> GetIsDeleted<TKey, TValue>()
  {
    static IsDeletedDelegate<TKey, TValue> Cast(object method) =>
        (IsDeletedDelegate<TKey, TValue>)method;
    TValue value = default;
    var result = value switch
    {
      byte => Cast(new IsDeletedDelegate<TKey, byte>(IsDeletedByte)),
      char => Cast(new IsDeletedDelegate<TKey, char>(IsDeletedChar)),
      DateTime => Cast(new IsDeletedDelegate<TKey, DateTime>(IsDeletedDateTime)),
      decimal => Cast(new IsDeletedDelegate<TKey, decimal>(IsDeletedDecimal)),
      double => Cast(new IsDeletedDelegate<TKey, double>(IsDeletedDouble)),
      short => Cast(new IsDeletedDelegate<TKey, short>(IsDeletedShort)),
      ushort => Cast(new IsDeletedDelegate<TKey, ushort>(IsDeletedUShort)),
      int => Cast(new IsDeletedDelegate<TKey, int>(IsDeletedInt)),
      uint => Cast(new IsDeletedDelegate<TKey, uint>(IsDeletedUInt)),
      long => Cast(new IsDeletedDelegate<TKey, long>(IsDeletedLong)),
      ulong => Cast(new IsDeletedDelegate<TKey, ulong>(IsDeletedULong)),
      Guid => Cast(new IsDeletedDelegate<TKey, Guid>(IsDeletedGuid)),
      _ => IsDeletedDefault
    };

    if (typeof(TValue) == typeof(Memory<byte>))
      result = Cast(new IsDeletedDelegate<TKey, Memory<byte>>(IsDeletedMemoryByte));
    if (!typeof(TValue).IsValueType)
      return IsDeletedReferenceType;
    return result;
  }

  /// <summary>
  /// Returns a delegate that marks a value of a specific type as deleted by setting it to its default value.
  /// </summary>
  /// <typeparam name="TValue">The type of the value.</typeparam>
  /// <returns>A delegate that marks the value as deleted.</returns>
  public static MarkValueDeletedDelegate<TValue> GetMarkValueDeleted<TValue>()
  {
    return MarkValueDeletedDefault;
  }
}
