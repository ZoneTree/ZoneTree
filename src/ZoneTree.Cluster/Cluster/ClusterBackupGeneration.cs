namespace ZoneTree.Cluster;

/// <summary>
/// A cluster backup generation that began: the catalog and each domain's cut it covers, and
/// the backups of its columns. Disposing it releases the columns' captures and lets drops go on.
/// </summary>
internal sealed class ClusterBackupGeneration : IDisposable
{
  readonly Action End;
  int Disposed;

  internal ClusterBackupGeneration(ClusterBackupPublishing publishing, IReadOnlyList<ColumnBackup> backups,
      CatalogState catalog, IReadOnlyList<ClusterBackupCut> cuts, Action end)
  {
    Publishing = publishing;
    Backups = backups;
    Catalog = catalog;
    Cuts = cuts;
    End = end;
  }

  internal ClusterBackupPublishing Publishing { get; }

  internal IReadOnlyList<ColumnBackup> Backups { get; }

  internal CatalogState Catalog { get; }

  internal IReadOnlyList<ClusterBackupCut> Cuts { get; }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref Disposed, 1) != 0)
      return;
    foreach (var backup in Backups)
      backup.Dispose();
    End();
  }
}
