using ZoneTree.Comparers;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.Cluster;

/// <summary>
/// Declares a column backed by the MVCC engine of a transactional ZoneTree, with its types
/// and storage options. Its transactions cover only this column.
/// </summary>
public sealed class StandaloneColumnDefinition<TKey, TValue> : ColumnDefinition
{
  internal override Type KeyType => typeof(TKey);
  internal override Type ValueType => typeof(TValue);
  internal override ColumnKind Kind => ColumnKind.Standalone;

  public StandaloneColumnDefinition(string name) : base(name) { }

  /// <summary>The key comparer. Null uses the default comparer of the key type.</summary>
  public IRefComparer<TKey> Comparer { get; init; }

  /// <summary>The key serializer. Null uses the default serializer of the key type.</summary>
  public ISerializer<TKey> KeySerializer { get; init; }

  /// <summary>The value serializer. Null uses the default serializer of the value type.</summary>
  public ISerializer<TValue> ValueSerializer { get; init; }

  /// <summary>
  /// Configures the column storage on the host, such as segment, cache, compression and
  /// WAL tuning. Runs after the cluster's StorageDefaults hook. Clients do not use it.
  /// </summary>
  public Action<ZoneTreeFactory<TKey, TValue>> ConfigureStorage { get; init; }

  internal override Column Open(
      ZoneTreeCluster cluster, int index, bool create, IColumnStorageConfigurator storageDefaults)
  {
    var factory = new ZoneTreeFactory<TKey, TValue>()
        .SetLogger(cluster.Logger)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.Shared);
    ColumnSchema.Apply(factory, Comparer, KeySerializer, ValueSerializer);
    storageDefaults?.Configure(this, factory);
    ConfigureStorage?.Invoke(factory);
    ColumnSchema.Verify(factory, Name, Comparer, KeySerializer, ValueSerializer);
    factory.SetDataDirectory(Path.Combine(cluster.DataDirectory, $"column-{index:D6}"));
    var tree = create ? factory.CreateTransactional() : factory.OpenTransactional();
    return new StandaloneColumn<TKey, TValue>(cluster, index, Name, tree);
  }
}
