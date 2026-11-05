namespace ZoneTree.Cluster;

/// <summary>
/// Declares a column backed by the MVCC engine of a transactional ZoneTree, with its types
/// and storage options. Its transactions cover only this column.
/// </summary>
public sealed class StandaloneColumnDefinition<TKey, TValue> : ColumnDefinition,
    ITypedColumnDefinition<TKey, TValue>
{
  internal override Type KeyType => typeof(TKey);
  internal override Type ValueType => typeof(TValue);
  public override ColumnKind Kind => ColumnKind.Standalone;

  /// <summary>Declares one column. Column names are unique in the cluster.</summary>
  public StandaloneColumnDefinition(string name) : base(name) { }

  /// <summary>
  /// Configures the column storage on the host, such as segment, cache, compression and
  /// WAL tuning. Runs after the cluster's StorageDefaults hook, when this definition opens
  /// the column in this process. It cannot replace the comparer or serializers.
  /// </summary>
  public Action<ZoneTreeFactory<TKey, TValue>> ConfigureStorage { get; init; }

  internal override void ValidateEncodings(ClusterEncodingRegistry encodings)
      => encodings.Resolve<TKey, TValue>(this);

  internal override Column GetOrCreate(ZoneTreeCluster cluster, string domain)
      => cluster.GetOrCreateColumn<TKey, TValue>(this, domain, getOrCreate: true);

  Column ITypedColumnDefinition<TKey, TValue>.OpenStorage(
      ZoneTreeCluster cluster, ColumnRecord record, ZoneTreeFactory<TKey, TValue> factory)
  {
    var tree = record.State == ColumnState.Creating
        ? factory.OpenOrCreateTransactional()
        : factory.OpenTransactional();
    return new StandaloneColumn<TKey, TValue>(cluster, record.Id, Name, tree);
  }
}
