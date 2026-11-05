using ZoneTree.Options;

namespace ZoneTree.Cluster;

/// <summary>
/// Prepares the storage factory of a column: its registered encodings, then the host's
/// storage hooks. Preparing runs before a new column enters the catalog, so a failing hook
/// leaves no column behind.
/// </summary>
internal static class ColumnSchema
{
  internal static ZoneTreeFactory<TKey, TValue> Prepare<TKey, TValue>(
      ZoneTreeCluster cluster,
      ColumnDefinition column,
      ColumnEncodings<TKey, TValue> encodings,
      Action<ZoneTreeFactory<TKey, TValue>> configureStorage)
  {
    var factory = new ZoneTreeFactory<TKey, TValue>()
        .SetLogger(cluster.Logger)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.Shared)
        .SetComparer(encodings.Comparer)
        .SetKeySerializer(encodings.KeySerializer)
        .SetValueSerializer(encodings.ValueSerializer);
    cluster.StorageDefaults?.Configure(column, factory);
    configureStorage?.Invoke(factory);
    // Every process that opens the column encodes and orders keys with the registered
    // encodings, so a storage hook that replaced them would make them disagree.
    var options = factory.Options;
    if (!ReferenceEquals(options.Comparer, encodings.Comparer) ||
        !ReferenceEquals(options.KeySerializer, encodings.KeySerializer) ||
        !ReferenceEquals(options.ValueSerializer, encodings.ValueSerializer))
      throw new ArgumentException(
          $"The storage configuration of column '{column.Name}' replaced its comparer or serializers. Name them in the column definition instead.");
    return factory;
  }
}
