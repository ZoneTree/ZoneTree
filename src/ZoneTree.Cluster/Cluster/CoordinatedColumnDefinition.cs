using ZoneTree.Comparers;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.Cluster;

/// <summary>A column that takes part in the cluster transactions of its domain.</summary>
public abstract class CoordinatedColumnDefinition : ColumnDefinition
{
  internal override ColumnKind Kind => ColumnKind.Coordinated;

  private protected CoordinatedColumnDefinition(string name) : base(name) { }
}

/// <summary>Declares a column that takes part in cluster transactions, with its types and storage options.</summary>
public sealed class CoordinatedColumnDefinition<TKey, TValue> : CoordinatedColumnDefinition
{
  internal override Type KeyType => typeof(TKey);
  internal override Type ValueType => typeof(TValue);

  /// <summary>Declares one column. Duplicate names are rejected when opening the cluster.</summary>
  public CoordinatedColumnDefinition(string name) : base(name) { }

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
    return new CoordinatedColumn<TKey, TValue>(cluster, index, Name,
        ClusterMvccZoneTreeFactory.Open(factory, canCreate: create, canOpen: !create));
  }
}
