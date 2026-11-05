using ZoneTree.Logger;
using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal sealed class StandaloneColumn<TKey, TValue> : Column,
    IStandaloneColumn<TKey, TValue>
{
  readonly ITransactionalZoneTree<TKey, TValue> Tree;
  volatile bool Disposed;

  internal StandaloneColumn(ZoneTreeCluster cluster, int index, string name,
      ITransactionalZoneTree<TKey, TValue> tree) : base(cluster, index, name) => Tree = tree;

  IZoneTreeCluster IStandaloneColumn<TKey, TValue>.Cluster => Cluster;
  public ILogger Logger => Tree.Logger;
  public ITransactionalZoneTreeMaintenance<TKey, TValue> Maintenance => Tree.Maintenance;
  internal override int AbortedTransactionCount => Tree.Maintenance.AbortedTransactionCount;
  internal override int ConflictIndexCount => Tree.Maintenance.ConflictIndexCount;

  public ITransaction<TKey, TValue> BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable)
  {
    EnsureOpen();
    return Tree.BeginTransaction(isolationLevel);
  }

  public IReadTransaction<TKey, TValue> BeginReadOnlyTransaction()
  {
    EnsureOpen();
    return Tree.BeginReadOnlyTransaction();
  }

  public Task SyncWALAsync(CancellationToken cancellationToken = default)
  {
    EnsureOpen();
    return Tree.SyncWALAsync(cancellationToken);
  }

  internal override Task SyncWALAsync() => SyncWALAsync(default);

  public bool IsReadOnly
  {
    get => Tree.IsReadOnly;
    set
    {
      EnsureOpen();
      Tree.IsReadOnly = value;
    }
  }

  internal override void SetReadOnly(bool value) => IsReadOnly = value;

  TimeSpan ITransactionalZoneTree<TKey, TValue>.AutoSyncInterval
  {
    get => AutoSyncInterval;
    set => AutoSyncInterval = value;
  }

  internal override TimeSpan AutoSyncInterval
  {
    get => Tree.AutoSyncInterval;
    set
    {
      EnsureOpen();
      Tree.AutoSyncInterval = value;
    }
  }

  internal override ColumnView CreateView(bool writing, TransactionIsolationLevel isolation)
      => throw new InvalidOperationException("Standalone columns cannot participate in cluster transactions.");

  internal override void SaveMetaData() => Tree.Maintenance.SaveMetaData();

  public override IMaintainer CreateMaintainer()
  {
    EnsureOpen();
    return Tree.CreateMaintainer();
  }

  internal override void Drop()
  {
    EnsureOpen();
    Disposed = true;
    Tree.Maintenance.Drop();
  }

  // The cluster disposes its columns through this override.
  public override void Dispose()
  {
    if (Disposed)
      return;
    Disposed = true;
    Tree.Dispose();
  }

  // A handle does not own the column, so disposing it through the interface does nothing.
  void IDisposable.Dispose()
  {
  }

  void EnsureOpen() => ObjectDisposedException.ThrowIf(Disposed, this);
}
