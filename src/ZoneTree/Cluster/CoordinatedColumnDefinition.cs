using ZoneTree.Options;

namespace ZoneTree.Cluster;

/// <summary>Declares a column that takes part in cluster transactions, with its types and storage options.</summary>
public sealed class CoordinatedColumnDefinition<TKey, TValue> : ColumnDefinition
{
  internal override Type KeyType => typeof(TKey);
  internal override Type ValueType => typeof(TValue);
  internal override ColumnKind Kind => ColumnKind.Coordinated;

  /// <summary>Declares one column. Duplicate names are rejected when opening the cluster.</summary>
  public CoordinatedColumnDefinition(string name) : base(name) { }

  /// <summary>
  /// Configures the column's storage options, such as comparers, serializers and compression.
  /// Runs after the cluster's StorageDefaults hook.
  /// </summary>
  public Action<ZoneTreeFactory<TKey, TValue>> ConfigureStorage { get; init; }

  internal override Column Open(
      ZoneTreeCluster cluster, int index, bool create, IColumnStorageConfigurator storageDefaults)
  {
    var factory = new ZoneTreeFactory<TKey, TValue>()
        .SetLogger(cluster.Logger)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.Shared);
    storageDefaults?.Configure(this, factory);
    ConfigureStorage?.Invoke(factory);
    factory.SetDataDirectory(Path.Combine(cluster.DataDirectory, $"column-{index:D6}"));
    return new CoordinatedColumn<TKey, TValue>(cluster, index, Name,
        factory.OpenClusterColumn(canCreate: create, canOpen: !create));
  }
}
