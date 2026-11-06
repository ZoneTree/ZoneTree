namespace ZoneTree.Cluster;

/// <summary>Declares a non-transactional column's types and storage options.</summary>
public sealed class RawColumnDefinition<TKey, TValue> : ColumnDefinition,
    ITypedColumnDefinition<TKey, TValue>
{
  internal override Type KeyType => typeof(TKey);
  internal override Type ValueType => typeof(TValue);
  public override ColumnKind Kind => ColumnKind.Raw;

  /// <summary>Declares one column. Column names are unique in the cluster.</summary>
  public RawColumnDefinition(string name) : base(name) { }

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

  internal override async Task RestoreStorageAsync(ColumnStorageSettings settings,
      ClusterEncodingRegistry encodings, ColumnRecord record, string directory,
      Backup.ILiveBackupSource source, long generationId)
  {
    var factory = PrepareRestore(settings, encodings, record, directory, ConfigureStorage);
    using var tree = await factory.RestoreFromLiveBackupGeneration(source, generationId).ConfigureAwait(false);
  }

  Column ITypedColumnDefinition<TKey, TValue>.OpenStorage(
      ZoneTreeCluster cluster, ColumnRecord record, ZoneTreeFactory<TKey, TValue> factory)
  {
    var tree = record.State == ColumnState.Creating ? factory.OpenOrCreate() : factory.Open();
    return new RawColumn<TKey, TValue>(cluster, record.Id, Name, tree);
  }
}
