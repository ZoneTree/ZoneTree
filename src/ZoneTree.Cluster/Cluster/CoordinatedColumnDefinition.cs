namespace ZoneTree.Cluster;

/// <summary>A column that takes part in the cluster transactions of its domain.</summary>
public abstract class CoordinatedColumnDefinition : ColumnDefinition
{
  public sealed override ColumnKind Kind => ColumnKind.Coordinated;

  private protected CoordinatedColumnDefinition(string name) : base(name) { }
}

/// <summary>Declares a column that takes part in cluster transactions, with its types and storage options.</summary>
public sealed class CoordinatedColumnDefinition<TKey, TValue> : CoordinatedColumnDefinition,
    ITypedColumnDefinition<TKey, TValue>
{
  internal override Type KeyType => typeof(TKey);
  internal override Type ValueType => typeof(TValue);

  /// <summary>Declares one column. Column names are unique in the cluster.</summary>
  public CoordinatedColumnDefinition(string name) : base(name) { }

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

  internal override async ValueTask RestoreStorageAsync(ColumnStorageSettings settings,
      ClusterEncodingRegistry encodings, ColumnRecord record, string directory,
      Backup.ILiveBackupSource source, long generationId)
  {
    var factory = PrepareRestore(settings, encodings, record, directory, ConfigureStorage);
    await ClusterMvccZoneTreeFactory.RestoreAsync(factory, source, generationId).ConfigureAwait(false);
  }

  Column ITypedColumnDefinition<TKey, TValue>.OpenStorage(
      ZoneTreeCluster cluster, ColumnRecord record, ZoneTreeFactory<TKey, TValue> factory)
  {
    var coordinator = cluster.DomainOf(record).Coordinator;
    var domain = cluster.Catalog.State.FindDomain(record.DomainId);
    var tree = ClusterMvccZoneTreeFactory.Open(factory,
        canCreate: record.State == ColumnState.Creating, canOpen: true,
        coordinator, domain.IsRolledBack);
    return new CoordinatedColumn<TKey, TValue>(cluster, record.Id, Name, tree);
  }
}
