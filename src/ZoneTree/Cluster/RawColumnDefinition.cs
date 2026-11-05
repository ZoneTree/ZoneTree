using ZoneTree.Options;

namespace ZoneTree.Cluster;

/// <summary>Declares a non-transactional column's types and storage options.</summary>
public sealed class RawColumnDefinition<TKey, TValue> : ColumnDefinition
{
  internal override Type KeyType => typeof(TKey);
  internal override Type ValueType => typeof(TValue);
  internal override ColumnKind Kind => ColumnKind.Raw;

  public RawColumnDefinition(string name) : base(name) { }

  /// <summary>Configures the column's plain ZoneTree storage after the cluster's StorageDefaults hook.</summary>
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
    var tree = create ? factory.Create() : factory.Open();
    return new RawColumn<TKey, TValue>(cluster, index, Name, tree);
  }
}
