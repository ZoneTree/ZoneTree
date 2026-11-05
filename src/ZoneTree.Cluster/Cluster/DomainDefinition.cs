namespace ZoneTree.Cluster;

/// <summary>
/// Declares a domain: the coordinated columns that cluster transactions can span together.
/// Opening the cluster creates the domain and its columns if they do not exist.
/// </summary>
public sealed class DomainDefinition
{
  /// <summary>The domain name, unique in the cluster and compared using ordinal comparison.</summary>
  public string Name { get; }

  /// <summary>
  /// The domain's declared columns, with names unique across the cluster. The domain may have
  /// more columns, created at run time.
  /// </summary>
  public IReadOnlyList<CoordinatedColumnDefinition> Columns { get; init; }
      = Array.Empty<CoordinatedColumnDefinition>();

  public DomainDefinition(string name)
  {
    ClusterCatalog.ValidateName(name, "domain");
    Name = name;
  }
}
