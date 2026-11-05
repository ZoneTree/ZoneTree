namespace ZoneTree.Cluster;

/// <summary>
/// The schema of a cluster: its domains and columns, stored in the cluster catalog, which is
/// the schema's single source of truth.
/// </summary>
/// <remarks>
/// Every change is durable when its task completes and has its own schema epoch. Changes from
/// concurrent callers are ordered by the catalog: of two creations of one name, one creates
/// the domain or column and the other finds it. GetOrCreate returns an existing domain or a
/// column with the same definition, and fails with <see cref="SchemaConflictException"/> for
/// a column with another kind, domain or encoding. A transaction can use a column created
/// after it began; the column has no data in its snapshot. A read-only cluster refuses
/// schema changes.
/// </remarks>
public interface IClusterSchema
{
  /// <summary>Creates a domain.</summary>
  /// <exception cref="SchemaObjectAlreadyExistsException">The domain exists.</exception>
  ValueTask<IClusterDomain> CreateDomainAsync(string name, CancellationToken cancellationToken = default);

  /// <summary>Gets a domain, creating it if it does not exist.</summary>
  ValueTask<IClusterDomain> GetOrCreateDomainAsync(string name, CancellationToken cancellationToken = default);

  /// <summary>Creates a coordinated column in a domain.</summary>
  /// <exception cref="SchemaObjectAlreadyExistsException">A column with the name exists.</exception>
  /// <exception cref="KeyNotFoundException">The domain does not exist.</exception>
  /// <exception cref="ColumnEncodingException">An encoding of the definition is not registered for its types.</exception>
  ValueTask<ICoordinatedColumn<TKey, TValue>> CreateColumnAsync<TKey, TValue>(
      string domain, CoordinatedColumnDefinition<TKey, TValue> definition,
      CancellationToken cancellationToken = default);

  /// <summary>Gets a coordinated column of a domain, creating it if it does not exist.</summary>
  /// <exception cref="SchemaConflictException">The column exists with another definition.</exception>
  ValueTask<ICoordinatedColumn<TKey, TValue>> GetOrCreateColumnAsync<TKey, TValue>(
      string domain, CoordinatedColumnDefinition<TKey, TValue> definition,
      CancellationToken cancellationToken = default);

  /// <summary>Creates a standalone column.</summary>
  /// <exception cref="SchemaObjectAlreadyExistsException">A column with the name exists.</exception>
  ValueTask<IStandaloneColumn<TKey, TValue>> CreateColumnAsync<TKey, TValue>(
      StandaloneColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default);

  /// <summary>Gets a standalone column, creating it if it does not exist.</summary>
  /// <exception cref="SchemaConflictException">The column exists with another definition.</exception>
  ValueTask<IStandaloneColumn<TKey, TValue>> GetOrCreateColumnAsync<TKey, TValue>(
      StandaloneColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default);

  /// <summary>Creates a raw column.</summary>
  /// <exception cref="SchemaObjectAlreadyExistsException">A column with the name exists.</exception>
  ValueTask<IRawColumn<TKey, TValue>> CreateColumnAsync<TKey, TValue>(
      RawColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default);

  /// <summary>Gets a raw column, creating it if it does not exist.</summary>
  /// <exception cref="SchemaConflictException">The column exists with another definition.</exception>
  ValueTask<IRawColumn<TKey, TValue>> GetOrCreateColumnAsync<TKey, TValue>(
      RawColumnDefinition<TKey, TValue> definition, CancellationToken cancellationToken = default);

  /// <summary>Describes the current schema.</summary>
  ValueTask<ClusterSchemaDescription> DescribeAsync(CancellationToken cancellationToken = default);
}
