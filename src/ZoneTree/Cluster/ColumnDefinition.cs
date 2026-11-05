namespace ZoneTree.Cluster;

/// <summary>A column schema entry.</summary>
public abstract class ColumnDefinition
{
  /// <summary>The persisted column name, compared using ordinal comparison.</summary>
  public string Name { get; }

  internal abstract Type KeyType { get; }
  internal abstract Type ValueType { get; }
  internal abstract ColumnKind Kind { get; }
  internal abstract Column Open(
      ZoneTreeCluster cluster, int index, bool create, IColumnStorageConfigurator storageDefaults);

  private protected ColumnDefinition(string name)
  {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("A column needs a nonempty name.", nameof(name));
    Name = name;
  }
}
