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

  internal override ColumnView CreateView(bool writing, TransactionIsolationLevel isolation)
  {
    var view = (ColumnView)(writing
        ? Tree.BeginTransaction(isolation) : Tree.BeginReadOnlyTransaction());
    view.Column = this;
    return view;
  }

  internal override Task SyncWALAsync() => Tree.SyncWALAsync();
  internal override void SaveMetaData() => Tree.SaveMetaData();
  internal override void Drop() => Tree.Drop();
  public override IMaintainer CreateMaintainer() => Tree.CreateMaintainer();
  public override void Dispose() => Tree.Dispose();
}
