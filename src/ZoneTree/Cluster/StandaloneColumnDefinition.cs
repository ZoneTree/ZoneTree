using ZoneTree.Options;

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

  /// <summary>Configures the column's transactional ZoneTree storage after the cluster's StorageDefaults hook.</summary>
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
    var tree = create ? factory.CreateTransactional() : factory.OpenTransactional();
    return new StandaloneColumn<TKey, TValue>(cluster, index, Name, tree);
  }
}
