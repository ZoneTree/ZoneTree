namespace ZoneTree.Cluster;

/// <summary>A snapshot of a cluster's schema.</summary>
public sealed class ClusterSchemaDescription
{
  /// <summary>The id of the cluster, fixed when it was created.</summary>
  public Guid ClusterId { get; init; }

  /// <summary>The schema epoch, which increases with every schema change.</summary>
  public long SchemaEpoch { get; init; }

  /// <summary>The domains in creation order.</summary>
  public IReadOnlyList<DomainDescription> Domains { get; init; } = Array.Empty<DomainDescription>();

  /// <summary>The columns of every kind in creation order.</summary>
  public IReadOnlyList<ColumnDescription> Columns { get; init; } = Array.Empty<ColumnDescription>();
}

/// <summary>A domain of a cluster schema.</summary>
public sealed class DomainDescription
{
  /// <summary>The domain name.</summary>
  public string Name { get; init; }

  /// <summary>The names of the domain's columns in creation order.</summary>
  public IReadOnlyList<string> ColumnNames { get; init; } = Array.Empty<string>();
}

/// <summary>A column of a cluster schema.</summary>
public sealed class ColumnDescription
{
  /// <summary>The column name.</summary>
  public string Name { get; init; }

  /// <summary>The kind of the column.</summary>
  public ColumnKind Kind { get; init; }

  /// <summary>The domain of a coordinated column; null for other kinds.</summary>
  public string Domain { get; init; }

  /// <summary>The key serializer the column was created with.</summary>
  public ColumnEncoding KeySerializer { get; init; }

  /// <summary>The value serializer the column was created with.</summary>
  public ColumnEncoding ValueSerializer { get; init; }

  /// <summary>The key comparer the column was created with.</summary>
  public ColumnEncoding Comparer { get; init; }

  /// <summary>The name of the key type the column was created with, for information.</summary>
  public string KeyType { get; init; }

  /// <summary>The name of the value type the column was created with, for information.</summary>
  public string ValueType { get; init; }
}
