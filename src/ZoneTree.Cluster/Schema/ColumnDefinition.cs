namespace ZoneTree.Cluster;

/// <summary>
/// A column of the cluster schema: its name, kind and encodings, and how its storage is
/// configured on the host.
/// </summary>
/// <remarks>
/// The cluster catalog stores the name, kind, domain and encodings when the column is
/// created. Declaring or creating the column again with the same values opens it; other
/// values fail with <see cref="SchemaConflictException"/>.
/// </remarks>
public abstract class ColumnDefinition
{
  /// <summary>The column name, unique in the cluster and compared using ordinal comparison.</summary>
  public string Name { get; }

  /// <summary>The kind of the column.</summary>
  public abstract ColumnKind Kind { get; }

  /// <summary>
  /// The id of the key serializer in the cluster's encoding registry. Null uses the built-in
  /// serializer of the key type.
  /// </summary>
  public string KeySerializerId { get; init; }

  /// <summary>
  /// The id of the value serializer in the cluster's encoding registry. Null uses the built-in
  /// serializer of the value type.
  /// </summary>
  public string ValueSerializerId { get; init; }

  /// <summary>
  /// The id of the key comparer in the cluster's encoding registry. Null uses the built-in
  /// ascending comparer of the key type.
  /// </summary>
  public string ComparerId { get; init; }

  internal abstract Type KeyType { get; }
  internal abstract Type ValueType { get; }

  /// <summary>Checks that the definition's encodings resolve for its types.</summary>
  internal abstract void ValidateEncodings(ClusterEncodingRegistry encodings);

  /// <summary>Opens the column, creating it in the catalog first if it does not exist.</summary>
  internal abstract Column GetOrCreate(ZoneTreeCluster cluster, string domain);

  /// <summary>
  /// Restores a backup generation of the column's storage into its directory, with the
  /// column's types and encodings. The column opens afterward, with the cluster.
  /// </summary>
  internal abstract Task RestoreStorageAsync(ColumnStorageSettings settings, ClusterEncodingRegistry encodings,
      ColumnRecord record, string directory, Backup.ILiveBackupSource source, long generationId);

  /// <summary>Prepares the storage factory of a restored column, with the encodings it was created with.</summary>
  private protected ZoneTreeFactory<TKey, TValue> PrepareRestore<TKey, TValue>(ColumnStorageSettings settings,
      ClusterEncodingRegistry registry, ColumnRecord record, string directory,
      Action<ZoneTreeFactory<TKey, TValue>> configureStorage)
  {
    var encodings = registry.Resolve<TKey, TValue>(this);
    if (!encodings.Matches(record))
      throw new ColumnEncodingException(
          $"Column '{record.Name}' was created with {encodings.DescribeDifference(record)}.");
    return ColumnSchema.Prepare(settings, this, encodings, configureStorage).SetDataDirectory(directory);
  }

  private protected ColumnDefinition(string name)
  {
    ClusterCatalog.ValidateName(name, "column");
    Name = name;
  }
}

/// <summary>The typed part of a column definition, which opens the column's storage.</summary>
internal interface ITypedColumnDefinition<TKey, TValue>
{
  Action<ZoneTreeFactory<TKey, TValue>> ConfigureStorage { get; }

  /// <summary>
  /// Opens the storage of a catalog column with a prepared factory, creating the storage if
  /// the catalog says it may not exist yet.
  /// </summary>
  Column OpenStorage(ZoneTreeCluster cluster, ColumnRecord record, ZoneTreeFactory<TKey, TValue> factory);
}
