using ZoneTree.Exceptions;

namespace ZoneTree.Cluster;

/// <summary>
/// The cluster's catalog does not match its storage: it lost its records, or a column's storage
/// exists where the catalog has none. The cluster refuses to open, because starting over would
/// reuse the ids and directories of existing columns and could delete their data.
/// </summary>
public sealed class ClusterCatalogCorruptionException(string message) : ZoneTreeException(message);
