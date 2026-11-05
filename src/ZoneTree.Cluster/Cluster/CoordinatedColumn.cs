using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal sealed class CoordinatedColumn<TKey, TValue> : Column,
    ICoordinatedColumn<TKey, TValue>
{
  internal readonly ClusterMvccZoneTree<TKey, TValue> Tree;

  internal CoordinatedColumn(ZoneTreeCluster cluster, long id, string name,
      ClusterMvccZoneTree<TKey, TValue> tree) : base(cluster, id, name) => Tree = tree;

  IZoneTreeCluster ICoordinatedColumn<TKey, TValue>.Cluster => Cluster;
  IClusterDomain ICoordinatedColumn<TKey, TValue>.Domain => Domain;
  public ITransactionalZoneTreeMaintenance<TKey, TValue> Maintenance => Tree.Maintenance;
  internal override int AbortedTransactionCount => Tree.AbortedTransactionCount;
  internal override int ConflictIndexCount => Tree.ConflictIndexCount;
  internal override TimeSpan AutoSyncInterval
  {
    get => Tree.AutoSyncInterval;
    set => Tree.AutoSyncInterval = value;
  }

  // Views are created under the column's operation gate and registered, so a drop of the
  // column finds every view it has to end.
  internal override ColumnView CreateView(bool writing, TransactionIsolationLevel isolation)
  {
    BeginOperation();
    try
    {
      var view = (ColumnView)(writing
          ? Tree.BeginTransaction(isolation) : Tree.BeginReadOnlyTransaction());
      view.Column = this;
      AddDependent(view);
      return view;
    }
    finally
    {
      EndOperation();
    }
  }

  internal override Task SyncWALAsync() => Tree.SyncWALAsync();
  internal override void SaveMetaData() => Tree.SaveMetaData();
  protected override void DropTree() => Tree.Drop();
  public override IMaintainer CreateMaintainer() => Tree.CreateMaintainer();
  public override void Dispose()
  {
    StopOperations();
    Tree.Dispose();
  }
}
