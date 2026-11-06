namespace ZoneTree.Cluster;

/// <summary>
/// A client of a cluster with independently typed coordinated, standalone and raw columns.
/// The same client works for clusters in this process, in other processes and on other machines.
/// </summary>
/// <remarks>
/// Coordinated columns belong to domains, and cluster transactions begin from a domain.
/// Standalone columns have their own transactions, and raw columns are accessed directly.
/// The client, its domains and its column handles are thread-safe. In a remote cluster
/// the synchronous methods block the calling thread for a network round trip; prefer
/// the asynchronous ones. Storage maintenance and administration belong to
/// <see cref="IZoneTreeClusterHost"/>.
/// </remarks>
public interface IZoneTreeCluster : IDisposable, IAsyncDisposable
{
  /// <summary>
  /// The cluster schema, for creating and describing domains and columns at run time.
  /// </summary>
  IClusterSchema Schema { get; }

  /// <summary>Gets a domain of coordinated columns. Names are case-sensitive.</summary>
  /// <exception cref="SchemaObjectNotFoundException">The domain does not exist.</exception>
  IClusterDomain GetDomain(string name);

  /// <summary>Gets a typed handle to a column with its own transactions. Names are case-sensitive.</summary>
  /// <exception cref="SchemaObjectNotFoundException">The column does not exist.</exception>
  /// <exception cref="SchemaConflictException">The column has a different kind or types.</exception>
  IStandaloneColumn<TKey, TValue> GetStandaloneColumn<TKey, TValue>(string name);

  /// <summary>Gets a typed handle to a non-transactional column. Names are case-sensitive.</summary>
  /// <exception cref="SchemaObjectNotFoundException">The column does not exist.</exception>
  /// <exception cref="SchemaConflictException">The column has a different kind or types.</exception>
  IRawColumn<TKey, TValue> GetRawColumn<TKey, TValue>(string name);

  /// <summary>
  /// Makes completed raw writes and transactions committed before this call durable across power loss or an OS crash.
  /// </summary>
  Task SyncWALAsync(CancellationToken cancellationToken = default);
}
