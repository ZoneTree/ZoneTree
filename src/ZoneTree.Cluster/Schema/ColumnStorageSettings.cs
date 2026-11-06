using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Options;

namespace ZoneTree.Cluster;

/// <summary>
/// The cluster settings that prepare a column's storage: of an open cluster, or of a restore
/// before the cluster opens.
/// </summary>
internal sealed record ColumnStorageSettings(
    ILogger Logger,
    IFileStreamProvider FileStreamProvider,
    WriteAheadLogOptions CoordinatedWriteAheadLog,
    IColumnStorageConfigurator StorageDefaults)
{
  internal static ColumnStorageSettings From(ZoneTreeClusterOptions options) => new(
      options.Logger,
      options.FileStreamProvider ?? new LocalFileStreamProvider(),
      ZoneTreeCluster.CopyOf(options.CoordinatedWriteAheadLog),
      options.StorageDefaults);
}
