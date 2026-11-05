using System.Collections.Concurrent;
using ZoneTree.Comparers;
using ZoneTree.Serializers;

namespace ZoneTree.Cluster;

/// <summary>
/// The serializers and comparers that cluster columns name by id.
/// </summary>
/// <remarks>
/// The cluster schema stores the ids and versions of each column's encodings, so every
/// process that opens a column resolves the same ones. Built-in ids, starting with
/// "zonetree.", cover the types ZoneTree supports by default, and a definition that names
/// no encoding uses the built-in one of its type. Register custom encodings before opening
/// the cluster or creating a column that names them. Registered instances are shared by
/// every column that names them, so they must be thread-safe.
/// </remarks>
public sealed class ClusterEncodingRegistry
{
  sealed record Entry(Type Type, int Version, object Instance);

  readonly ConcurrentDictionary<string, Entry> Serializers = new(StringComparer.Ordinal);
  readonly ConcurrentDictionary<string, Entry> Comparers = new(StringComparer.Ordinal);
  readonly ConcurrentDictionary<Type, string> DefaultSerializers = new();
  readonly ConcurrentDictionary<Type, string> DefaultComparers = new();

  public ClusterEncodingRegistry()
  {
    AddBuiltIn("byte", new ByteSerializer(), new ByteComparerAscending(), null);
    AddBuiltIn("bool", new BooleanSerializer(), null, null);
    AddBuiltIn("char", new CharSerializer(), new CharComparerAscending(), null);
    AddBuiltIn("datetime", new DateTimeSerializer(), new DateTimeComparerAscending(), new DateTimeComparerDescending());
    AddBuiltIn("decimal", new DecimalSerializer(), new DecimalComparerAscending(), new DecimalComparerDescending());
    AddBuiltIn("double", new DoubleSerializer(), new DoubleComparerAscending(), new DoubleComparerDescending());
    AddBuiltIn("int16", new Int16Serializer(), new Int16ComparerAscending(), new Int16ComparerDescending());
    AddBuiltIn("uint16", new UInt16Serializer(), new UInt16ComparerAscending(), new UInt16ComparerDescending());
    AddBuiltIn("int32", new Int32Serializer(), new Int32ComparerAscending(), new Int32ComparerDescending());
    AddBuiltIn("uint32", new UInt32Serializer(), new UInt32ComparerAscending(), new UInt32ComparerDescending());
    AddBuiltIn("int64", new Int64Serializer(), new Int64ComparerAscending(), new Int64ComparerDescending());
    AddBuiltIn("uint64", new UInt64Serializer(), new UInt64ComparerAscending(), new UInt64ComparerDescending());
    AddBuiltIn("guid", new StructSerializer<Guid>(), new GuidComparerAscending(), null);
    AddBuiltIn("bytes", new ByteArraySerializer(),
        new ByteArrayLexicographicComparerAscending(), new ByteArrayLexicographicComparerDescending(),
        comparerName: "bytes.lexicographic");
    AddBuiltIn("string.utf8", new Utf8StringSerializer(),
        new StringOrdinalComparerAscending(), new StringOrdinalComparerDescending(),
        comparerName: "string.ordinal");
    // Culture-sensitive string orders can change with the operating system or ICU version,
    // which would reorder stored keys, so only the ordinal ones are built in.
    AddSerializer("zonetree.string.utf16", 1, new UnicodeStringSerializer());
    AddComparer("zonetree.string.ordinal-ignore-case.ascending", 1, new StringOrdinalIgnoreCaseComparerAscending());
  }

  /// <summary>
  /// Registers a serializer under an id. Increase the version whenever its serialized format
  /// changes: a column opens only with the version it was created with.
  /// </summary>
  /// <exception cref="ArgumentException">The id is empty or already registered.</exception>
  public ClusterEncodingRegistry AddSerializer<T>(string id, int version, ISerializer<T> serializer)
  {
    Add(Serializers, id, version, typeof(T), serializer);
    return this;
  }

  /// <summary>
  /// Registers a key comparer under an id. Increase the version whenever its order changes:
  /// a column opens only with the version it was created with.
  /// </summary>
  /// <exception cref="ArgumentException">The id is empty or already registered.</exception>
  public ClusterEncodingRegistry AddComparer<TKey>(string id, int version, IRefComparer<TKey> comparer)
  {
    Add(Comparers, id, version, typeof(TKey), comparer);
    return this;
  }

  static void Add(ConcurrentDictionary<string, Entry> entries, string id, int version, Type type, object instance)
  {
    if (string.IsNullOrWhiteSpace(id))
      throw new ArgumentException("An encoding needs a nonempty id.", nameof(id));
    if (version < 1)
      throw new ArgumentOutOfRangeException(nameof(version), version, "Encoding versions start at 1.");
    ArgumentNullException.ThrowIfNull(instance);
    if (!entries.TryAdd(id, new Entry(type, version, instance)))
      throw new ArgumentException($"The encoding '{id}' is already registered.", nameof(id));
  }

  void AddBuiltIn<T>(string name, ISerializer<T> serializer,
      IRefComparer<T> ascending, IRefComparer<T> descending, string comparerName = null)
  {
    var serializerId = "zonetree." + name;
    AddSerializer(serializerId, 1, serializer);
    DefaultSerializers[typeof(T)] = serializerId;
    if (ascending == null)
      return;
    var prefix = "zonetree." + (comparerName ?? name);
    AddComparer(prefix + ".ascending", 1, ascending);
    DefaultComparers[typeof(T)] = prefix + ".ascending";
    if (descending != null)
      AddComparer(prefix + ".descending", 1, descending);
  }

  /// <summary>
  /// Resolves the encodings a column definition names, or the built-in ones of its types.
  /// </summary>
  /// <exception cref="ColumnEncodingException">An encoding is missing or registered for another type.</exception>
  internal ColumnEncodings<TKey, TValue> Resolve<TKey, TValue>(ColumnDefinition column)
  {
    var (keySerializer, keySerializerEncoding) = Get<ISerializer<TKey>>(
        Serializers, DefaultSerializers, column.KeySerializerId, typeof(TKey), column.Name, "key serializer");
    var (valueSerializer, valueSerializerEncoding) = Get<ISerializer<TValue>>(
        Serializers, DefaultSerializers, column.ValueSerializerId, typeof(TValue), column.Name, "value serializer");
    var (comparer, comparerEncoding) = Get<IRefComparer<TKey>>(
        Comparers, DefaultComparers, column.ComparerId, typeof(TKey), column.Name, "comparer");
    return new(keySerializer, keySerializerEncoding, valueSerializer, valueSerializerEncoding,
        comparer, comparerEncoding);
  }

  /// <summary>
  /// Whether the registered encodings of a stored column are encodings of these key and value
  /// types. Unregistered encodings do not decide it; resolving them reports them.
  /// </summary>
  internal bool CanEncode<TKey, TValue>(ColumnRecord column)
      => IsFor(Serializers, column.KeySerializer.Id, typeof(TKey)) &&
         IsFor(Serializers, column.ValueSerializer.Id, typeof(TValue)) &&
         IsFor(Comparers, column.Comparer.Id, typeof(TKey));

  static bool IsFor(ConcurrentDictionary<string, Entry> entries, string id, Type type)
      => !entries.TryGetValue(id, out var entry) || entry.Type == type;

  static (T Instance, ColumnEncoding Encoding) Get<T>(
      ConcurrentDictionary<string, Entry> entries, ConcurrentDictionary<Type, string> defaults,
      string id, Type type, string column, string role)
  {
    if (id == null && !defaults.TryGetValue(type, out id))
      throw new ColumnEncodingException(
          $"Column '{column}' needs a {role}: {type} has no built-in one. Register one and name it in the column definition.");
    if (!entries.TryGetValue(id, out var entry))
      throw new ColumnEncodingException($"The {role} '{id}' of column '{column}' is not registered.");
    if (entry.Type != type)
      throw new ColumnEncodingException(
          $"The {role} '{id}' of column '{column}' is registered for {entry.Type}, not {type}.");
    return ((T)entry.Instance, new ColumnEncoding(id, entry.Version));
  }
}

/// <summary>The resolved serializers and comparer of a column, with their stored encodings.</summary>
internal sealed record ColumnEncodings<TKey, TValue>(
    ISerializer<TKey> KeySerializer, ColumnEncoding KeySerializerEncoding,
    ISerializer<TValue> ValueSerializer, ColumnEncoding ValueSerializerEncoding,
    IRefComparer<TKey> Comparer, ColumnEncoding ComparerEncoding)
{
  internal bool Matches(ColumnRecord column)
      => column.KeySerializer == KeySerializerEncoding &&
         column.ValueSerializer == ValueSerializerEncoding &&
         column.Comparer == ComparerEncoding;

  /// <summary>Describes how the stored encodings of a column differ from these.</summary>
  internal string DescribeDifference(ColumnRecord column)
  {
    if (column.KeySerializer != KeySerializerEncoding)
      return $"key serializer {column.KeySerializer}, not {KeySerializerEncoding}";
    if (column.ValueSerializer != ValueSerializerEncoding)
      return $"value serializer {column.ValueSerializer}, not {ValueSerializerEncoding}";
    return $"comparer {column.Comparer}, not {ComparerEncoding}";
  }
}
