namespace ZoneTree.Cluster;

/// <summary>
/// An immutable view of the cluster catalog. The catalog replaces it after each change, so
/// readers never lock.
/// </summary>
internal sealed class CatalogState
{
  readonly Dictionary<string, DomainRecord> DomainsByName;
  readonly Dictionary<long, DomainRecord> DomainsById;
  readonly Dictionary<string, ColumnRecord> ColumnsByName;

  internal ClusterRecord Cluster { get; }

  /// <summary>The domains in id order, which is creation order.</summary>
  internal IReadOnlyList<DomainRecord> Domains { get; }

  /// <summary>The columns in id order, which is creation order.</summary>
  internal IReadOnlyList<ColumnRecord> Columns { get; }

  internal CatalogState(ClusterRecord cluster,
      IEnumerable<DomainRecord> domains, IEnumerable<ColumnRecord> columns)
  {
    Cluster = cluster;
    Domains = domains.OrderBy(domain => domain.Id).ToArray();
    Columns = columns.OrderBy(column => column.Id).ToArray();
    DomainsByName = Domains.ToDictionary(domain => domain.Name, StringComparer.Ordinal);
    DomainsById = Domains.ToDictionary(domain => domain.Id);
    ColumnsByName = Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
  }

  internal DomainRecord FindDomain(string name)
      => name != null && DomainsByName.TryGetValue(name, out var domain) ? domain : null;

  internal DomainRecord FindDomain(long id)
      => DomainsById.TryGetValue(id, out var domain) ? domain : null;

  internal ColumnRecord FindColumn(string name)
      => name != null && ColumnsByName.TryGetValue(name, out var column) ? column : null;

  /// <summary>The names of a domain's columns, in creation order.</summary>
  internal IReadOnlyList<string> ColumnNamesOf(long domainId)
      => Columns.Where(column => column.DomainId == domainId).Select(column => column.Name).ToArray();

  internal CatalogState With(ClusterRecord cluster)
      => new(cluster, Domains, Columns);

  internal CatalogState With(ClusterRecord cluster, DomainRecord domain)
      => new(cluster, Domains.Where(existing => existing.Id != domain.Id).Append(domain), Columns);

  internal CatalogState With(IReadOnlyCollection<DomainRecord> domains)
  {
    var ids = domains.Select(domain => domain.Id).ToHashSet();
    return new(Cluster, Domains.Where(existing => !ids.Contains(existing.Id)).Concat(domains), Columns);
  }

  internal CatalogState With(ClusterRecord cluster, ColumnRecord column)
      => new(cluster, Domains, Columns.Where(existing => existing.Id != column.Id).Append(column));
}
