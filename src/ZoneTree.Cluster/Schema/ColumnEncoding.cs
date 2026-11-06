namespace ZoneTree.Cluster;

/// <summary>
/// A serializer or comparer of a column, as the cluster schema stores it: a registered id
/// and the version of its format.
/// </summary>
public readonly record struct ColumnEncoding(string Id, int Version)
{
  public override string ToString() => $"{Id}/{Version}";
}
