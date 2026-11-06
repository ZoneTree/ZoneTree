namespace ZoneTree.Cluster;

/// <summary>
/// An immutable view of the cluster catalog. The catalog replaces it after each change, so
/// readers never lock.
/// </summary>
/// <remarks>
/// Name lookups and the column lists hold live columns only. A dropped column keeps its
/// record, without its name, until its storage is deleted.
/// </remarks>
internal sealed class CatalogState
{
  readonly Dictionary<string, DomainRecord> DomainsByName;
  readonly Dictionary<long, DomainRecord> DomainsById;
  readonly Dictionary<string, ColumnRecord> ColumnsByName;
  readonly Dictionary<long, ColumnRecord> ColumnsById;
  readonly IReadOnlyList<ColumnRecord> AllColumns;

  internal ClusterRecord Cluster { get; }

  /// <summary>The domains in id order, which is creation order.</summary>
  internal IReadOnlyList<DomainRecord> Domains { get; }

  /// <summary>The live columns in id order, which is creation order.</summary>
  internal IReadOnlyList<ColumnRecord> Columns { get; }

  /// <summary>The dropped columns whose storage may still exist.</summary>
  internal IReadOnlyList<ColumnRecord> DroppedColumns { get; }

  internal CatalogState(ClusterRecord cluster,
      IEnumerable<DomainRecord> domains, IEnumerable<ColumnRecord> columns)
  {
    Cluster = cluster;
    Domains = domains.OrderBy(domain => domain.Id).ToArray();
    AllColumns = columns.OrderBy(column => column.Id).ToArray();
    Columns = AllColumns.Where(column => column.State != ColumnState.Dropped).ToArray();
    DroppedColumns = AllColumns.Where(column => column.State == ColumnState.Dropped).ToArray();
    DomainsByName = Domains.ToDictionary(domain => domain.Name, StringComparer.Ordinal);
    DomainsById = Domains.ToDictionary(domain => domain.Id);
    ColumnsByName = Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
    ColumnsById = AllColumns.ToDictionary(column => column.Id);
  }

  internal DomainRecord FindDomain(string name)
      => name != null && DomainsByName.TryGetValue(name, out var domain) ? domain : null;

  internal DomainRecord FindDomain(long id)
      => DomainsById.TryGetValue(id, out var domain) ? domain : null;

  /// <summary>Finds a live column by name.</summary>
  internal ColumnRecord FindColumn(string name)
      => name != null && ColumnsByName.TryGetValue(name, out var column) ? column : null;

  /// <summary>Finds a column by id, including a dropped one.</summary>
  internal ColumnRecord FindColumn(long id)
      => ColumnsById.TryGetValue(id, out var column) ? column : null;

  /// <summary>The names of a domain's live columns, in creation order.</summary>
  internal IReadOnlyList<string> ColumnNamesOf(long domainId)
      => Columns.Where(column => column.DomainId == domainId).Select(column => column.Name).ToArray();

  internal CatalogState With(ClusterRecord cluster, DomainRecord domain)
      => new(cluster, Domains.Where(existing => existing.Id != domain.Id).Append(domain), AllColumns);

  internal CatalogState With(IReadOnlyCollection<DomainRecord> domains)
  {
    var ids = domains.Select(domain => domain.Id).ToHashSet();
    return new(Cluster, Domains.Where(existing => !ids.Contains(existing.Id)).Concat(domains), AllColumns);
  }

  internal CatalogState With(ClusterRecord cluster, ColumnRecord column)
      => new(cluster, Domains, AllColumns.Where(existing => existing.Id != column.Id).Append(column));

  internal CatalogState WithoutDomain(ClusterRecord cluster, long id)
      => new(cluster, Domains.Where(existing => existing.Id != id), AllColumns);

  internal CatalogState WithoutColumn(long id)
      => new(Cluster, Domains, AllColumns.Where(existing => existing.Id != id));
}
