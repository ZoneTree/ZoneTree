namespace ZoneTree.Cluster;

/// <summary>The schema API of an in-process cluster, over its catalog.</summary>
internal sealed class ClusterSchema(ZoneTreeCluster cluster) : IClusterSchema
{
  // Schema changes commit durably, so they run off the caller's thread like other async operations.
  public ValueTask<IClusterDomain> CreateDomainAsync(string name, CancellationToken cancellationToken = default)
      => AsyncOperations.Run<IClusterDomain>(() => cluster.GetOrCreateDomain(name, getOrCreate: false), cancellationToken);

  public ValueTask<IClusterDomain> GetOrCreateDomainAsync(string name, CancellationToken cancellationToken = default)
      => AsyncOperations.Run<IClusterDomain>(() => cluster.GetOrCreateDomain(name, getOrCreate: true), cancellationToken);

  public ValueTask<ICoordinatedColumn<TKey, TValue>> CreateColumnAsync<TKey, TValue>(
      string domain, CoordinatedColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<ICoordinatedColumn<TKey, TValue>, TKey, TValue>(definition, domain, false, cancellationToken);

  public ValueTask<ICoordinatedColumn<TKey, TValue>> GetOrCreateColumnAsync<TKey, TValue>(
      string domain, CoordinatedColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<ICoordinatedColumn<TKey, TValue>, TKey, TValue>(definition, domain, true, cancellationToken);

  public ValueTask<IStandaloneColumn<TKey, TValue>> CreateColumnAsync<TKey, TValue>(
      StandaloneColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<IStandaloneColumn<TKey, TValue>, TKey, TValue>(definition, null, false, cancellationToken);

  public ValueTask<IStandaloneColumn<TKey, TValue>> GetOrCreateColumnAsync<TKey, TValue>(
      StandaloneColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<IStandaloneColumn<TKey, TValue>, TKey, TValue>(definition, null, true, cancellationToken);

  public ValueTask<IRawColumn<TKey, TValue>> CreateColumnAsync<TKey, TValue>(
      RawColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<IRawColumn<TKey, TValue>, TKey, TValue>(definition, null, false, cancellationToken);

  public ValueTask<IRawColumn<TKey, TValue>> GetOrCreateColumnAsync<TKey, TValue>(
      RawColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default)
      => Create<IRawColumn<TKey, TValue>, TKey, TValue>(definition, null, true, cancellationToken);

  ValueTask<THandle> Create<THandle, TKey, TValue>(
      ColumnDefinition definition, string domain, bool getOrCreate, CancellationToken cancellationToken)
      => AsyncOperations.Run(
          () => (THandle)(object)cluster.GetOrCreateColumn<TKey, TValue>(definition, domain, getOrCreate),
          cancellationToken);

  public ValueTask<ClusterSchemaDescription> DescribeAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    cluster.ThrowIfDisposed();
    var state = cluster.Catalog.State;
    return new(new ClusterSchemaDescription
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
