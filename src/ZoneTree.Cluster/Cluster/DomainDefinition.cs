namespace ZoneTree.Cluster;

/// <summary>
/// Declares a domain: the coordinated columns that cluster transactions can span together.
/// </summary>
public sealed class DomainDefinition
{
  /// <summary>The persisted domain name, compared using ordinal comparison.</summary>
  public string Name { get; }

  /// <summary>The domain's columns, with names unique across the cluster.</summary>
  public IReadOnlyList<CoordinatedColumnDefinition> Columns { get; init; }
      = Array.Empty<CoordinatedColumnDefinition>();

  public DomainDefinition(string name)
  {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("A domain needs a nonempty name.", nameof(name));
    Name = name;
  }
}
