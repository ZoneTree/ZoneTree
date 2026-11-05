using ZoneTree.Cluster;

namespace ZoneTree.UnitTests;

/// <summary>
/// Places the coordinated columns of a cluster test into one test domain.
/// </summary>
static class ClusterTestDomains
{
  public const string Name = "main";

  public static DomainDefinition[] Domains(IEnumerable<ColumnDefinition> columns)
  {
    var coordinated = columns.OfType<CoordinatedColumnDefinition>().ToArray();
    return coordinated.Length == 0 ? [] : [new DomainDefinition(Name) { Columns = coordinated }];
  }

  public static ColumnDefinition[] Others(IEnumerable<ColumnDefinition> columns)
      => columns.Where(column => column is not CoordinatedColumnDefinition).ToArray();
}
