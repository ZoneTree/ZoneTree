using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal sealed class CoordinatedColumn<TKey, TValue> : Column,
    ICoordinatedColumn<TKey, TValue>, ICoordinatedStorage
{
  internal readonly ClusterMvccZoneTree<TKey, TValue> Tree;

  internal CoordinatedColumn(ZoneTreeCluster cluster, long id, string name,
      ClusterMvccZoneTree<TKey, TValue> tree) : base(cluster, id, name) => Tree = tree;

  IZoneTreeCluster ICoordinatedColumn<TKey, TValue>.Cluster => Cluster;
  IClusterDomain ICoordinatedColumn<TKey, TValue>.Domain => Domain;
  public ITransactionalZoneTreeMaintenance<TKey, TValue> Maintenance => Tree.Maintenance;
  internal override int AbortedTransactionCount => Tree.AbortedTransactionCount;
  internal override int ConflictIndexCount => Tree.ConflictIndexCount;

  // The domain syncs all of its columns together.
  internal override TimeSpan AutoSyncInterval
  {
    get => Domain.Coordinator.AutoSyncInterval;
    set => Domain.Coordinator.AutoSyncInterval = value;
  }

  // Views are created under the column's operation gate and registered, so a drop of the
  // column finds every view it has to end.
  internal override ColumnView CreateView(bool writing, TransactionIsolationLevel isolation,
      long transactionId, long snapshotTimestamp)
  {
    BeginOperation();
    try
    {
      var view = (ColumnView)(writing
          ? Tree.BeginTransaction(isolation, transactionId, snapshotTimestamp)
          : Tree.BeginReadOnlyTransaction(snapshotTimestamp));
      view.Column = this;
      AddDependent(view);
      return view;
    }
    finally
    {
      EndOperation();
    }
  }

  internal override Task SyncWALAsync() => Domain.Coordinator.SyncAsync();

  void ICoordinatedStorage.SyncIfWritten()
  {
    if (IsDropped)
      return;
    try { Tree.SyncIfWritten(); }
    // A column that closed or was dropped meanwhile has nothing left to make durable.
    catch (global::System.ObjectDisposedException) when (IsDropped) { }
  }

  void ICoordinatedStorage.OnDomainDurable() => Tree.OnDomainDurable();

  TransactionOutcome ICoordinatedStorage.GetOutcome(long transactionId) => Tree.GetOutcome(transactionId);

  internal override void SaveMetaData() => Tree.SaveMetaData();
  protected override void DropTree() => Tree.Drop();
  public override IMaintainer CreateMaintainer() => Tree.CreateMaintainer();
  public override void Dispose()
  {
    StopOperations();
    Tree.Dispose();
  }
}
