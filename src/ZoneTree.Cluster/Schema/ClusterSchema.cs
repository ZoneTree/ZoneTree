namespace ZoneTree.Cluster;

/// <summary>The schema API of an in-process cluster, over its catalog.</summary>
internal sealed class ClusterSchema(ZoneTreeCluster cluster) : IClusterSchema
{
  // Schema changes commit durably, so they run off the caller's thread like other async operations.
  public Task<IClusterDomain> CreateDomainAsync(string name, CancellationToken cancellationToken = default)
      => AsyncOperations.Run<IClusterDomain>(() => cluster.GetOrCreateDomain(name, getOrCreate: false), cancellationToken);

  public Task<IClusterDomain> GetOrCreateDomainAsync(string name, CancellationToken cancellationToken = default)
      => AsyncOperations.Run<IClusterDomain>(() => cluster.GetOrCreateDomain(name, getOrCreate: true), cancellationToken);

  public Task<ICoordinatedColumn<TKey, TValue>> CreateColumnAsync<TKey, TValue>(
      string domain, CoordinatedColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<ICoordinatedColumn<TKey, TValue>, TKey, TValue>(definition, domain, false, cancellationToken);

  public Task<ICoordinatedColumn<TKey, TValue>> GetOrCreateColumnAsync<TKey, TValue>(
      string domain, CoordinatedColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<ICoordinatedColumn<TKey, TValue>, TKey, TValue>(definition, domain, true, cancellationToken);

  public Task<IStandaloneColumn<TKey, TValue>> CreateColumnAsync<TKey, TValue>(
      StandaloneColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<IStandaloneColumn<TKey, TValue>, TKey, TValue>(definition, null, false, cancellationToken);

  public Task<IStandaloneColumn<TKey, TValue>> GetOrCreateColumnAsync<TKey, TValue>(
      StandaloneColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<IStandaloneColumn<TKey, TValue>, TKey, TValue>(definition, null, true, cancellationToken);

  public Task<IRawColumn<TKey, TValue>> CreateColumnAsync<TKey, TValue>(
      RawColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<IRawColumn<TKey, TValue>, TKey, TValue>(definition, null, false, cancellationToken);

  public Task<IRawColumn<TKey, TValue>> GetOrCreateColumnAsync<TKey, TValue>(
      RawColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<IRawColumn<TKey, TValue>, TKey, TValue>(definition, null, true, cancellationToken);

  public Task<bool> DropColumnAsync(string name, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => cluster.DropColumn(name), cancellationToken);

  public Task RenameColumnAsync(string name, string newName, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => cluster.RenameColumn(name, newName), cancellationToken);

  public Task<bool> DropDomainAsync(string name, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => cluster.DropDomain(name), cancellationToken);

  public Task RenameDomainAsync(string name, string newName, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => cluster.RenameDomain(name, newName), cancellationToken);

  Task<THandle> Create<THandle, TKey, TValue>(
      ColumnDefinition definition, string domain, bool getOrCreate, CancellationToken cancellationToken)
      => AsyncOperations.Run(
          () => (THandle)(object)cluster.GetOrCreateColumn<TKey, TValue>(definition, domain, getOrCreate),
          cancellationToken);

  public Task<ClusterSchemaDescription> DescribeAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    cluster.ThrowIfDisposed();
    var state = cluster.Catalog.State;
    return Task.FromResult(new ClusterSchemaDescription
    {
      ClusterId = state.Cluster.ClusterId,
      SchemaEpoch = state.Cluster.SchemaEpoch,
      Domains = state.Domains
          .Select(domain => new DomainDescription { Name = domain.Name, ColumnNames = state.ColumnNamesOf(domain.Id) })
          .ToArray(),
      Columns = state.Columns
          .Select(column => new ColumnDescription
          {
            Name = column.Name,
            Kind = column.Kind,
            Domain = state.FindDomain(column.DomainId)?.Name,
            KeySerializer = column.KeySerializer,
            ValueSerializer = column.ValueSerializer,
            Comparer = column.Comparer,
            KeyType = column.KeyType,
            ValueType = column.ValueType
          })
          .ToArray()
    });
  }
}
