namespace ZoneTree.Cluster;

/// <summary>A column schema entry.</summary>
public abstract class ColumnDefinition
{
  /// <summary>The persisted column name, compared using ordinal comparison.</summary>
  public string Name { get; }

  internal abstract Type KeyType { get; }
  internal abstract Type ValueType { get; }
  internal virtual bool IsRaw => false;
  internal abstract Column Open(
      ZoneTreeCluster cluster, int index, bool create);

  private protected ColumnDefinition(string name)
  {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("A column needs a nonempty name.", nameof(name));
    Name = name;
  }
}

/// <summary>Declares a column's key and value types and optional storage tuning.</summary>
public sealed class ColumnDefinition<TKey, TValue> : ColumnDefinition
{
  internal override Type KeyType => typeof(TKey);
  internal override Type ValueType => typeof(TValue);

  /// <summary>Declares one column. Duplicate names are rejected when opening the cluster.</summary>
  public ColumnDefinition(string name) : base(name) { }

  /// <summary>
  /// Configures the column's storage options, such as comparers, serializers and compression.
  /// </summary>
  public Action<ZoneTreeFactory<TKey, TValue>> ConfigureStorage { get; init; }

  internal override Column Open(
      ZoneTreeCluster cluster, int index, bool create)
  {
    var factory = new ZoneTreeFactory<TKey, TValue>()
        .SetLogger(cluster.Logger)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = Options.WriteAheadLogMode.Shared);
    ConfigureStorage?.Invoke(factory);
    factory.SetDataDirectory(Path.Combine(cluster.DataDirectory, $"column-{index:D6}"));
    return new Column<TKey, TValue>(cluster, index, Name,
        factory.OpenClusterColumn(canCreate: create, canOpen: !create));
  }
}
