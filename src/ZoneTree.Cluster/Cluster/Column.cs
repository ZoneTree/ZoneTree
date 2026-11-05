using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal abstract class Column : IDisposable
{
  internal readonly ZoneTreeCluster Cluster;
  internal readonly int Index;
  // TODO: Revisit this extra gate when column validation and preparation can be atomic.
  // It currently keeps another cluster transaction from committing to a column between
  // multi-column conflict preflight and commit startup, which could otherwise cause a
  // late conflict after an earlier column has committed. Single-column commits must
  // respect it too. Each coordinated column owns a separate MVCC tree; the locks
  // inside those trees do not cover this multi-column preflight and commit sequence.
  internal readonly object CommitGate = new();
  public string Name { get; }

  /// <summary>The domain of a coordinated column; null for other kinds.</summary>
  internal ClusterDomain Domain;

  protected Column(ZoneTreeCluster cluster, int index, string name)
  {
    Cluster = cluster;
    Index = index;
    Name = name;
  }

  internal abstract ColumnView CreateView(bool writing, TransactionIsolationLevel isolation);
  internal abstract Task SyncWALAsync();
  internal abstract TimeSpan AutoSyncInterval { get; set; }
  internal abstract int AbortedTransactionCount { get; }
  internal abstract int ConflictIndexCount { get; }
  internal virtual void SetReadOnly(bool value) { }
  internal abstract void SaveMetaData();
  internal abstract void Drop();
  public abstract IMaintainer CreateMaintainer();
  public abstract void Dispose();
}
