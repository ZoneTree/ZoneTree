using ZoneTree.Logger;

namespace ZoneTree.Cluster;

/// <summary>Configuration of a client that connects to a cluster hosted elsewhere.</summary>
public sealed class ZoneTreeClusterConnectionOptions
{
  /// <summary>The addresses of the cluster hosts to connect to.</summary>
  public IReadOnlyList<Uri> Endpoints { get; init; } = Array.Empty<Uri>();

  /// <summary>
  /// The domains the client uses. Their column definitions give the key and value
  /// serializers and comparers; storage configuration stays with the hosts.
  /// </summary>
  public IReadOnlyList<DomainDefinition> Domains { get; init; } = Array.Empty<DomainDefinition>();

  /// <summary>The standalone and raw columns the client uses, with their serializers and comparers.</summary>
  public IReadOnlyList<ColumnDefinition> Columns { get; init; } = Array.Empty<ColumnDefinition>();

  /// <summary>The longest wait for a connection to the hosts.</summary>
  public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>The longest wait for one operation; a commit that times out ends in doubt.</summary>
  public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>The client logger.</summary>
  public ILogger Logger { get; init; } = new ConsoleLogger();
}
