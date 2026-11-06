using ZoneTree.Transactional;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Cluster;

internal sealed class CoordinatedColumn<TKey, TValue> : Column,
    ICoordinatedColumn<TKey, TValue>, ICoordinatedStorage
{
  internal readonly ClusterMvccZoneTree<TKey, TValue> Tree;

  internal CoordinatedColumn(ZoneTreeCluster cluster, long id, string name,
      ClusterMvccZoneTree<TKey, TValue> tree) : base(cluster, id, name)
  {
    Tree = tree;
    tree.Storage.Maintenance.OnMergeOperationEnded += (_, result) => OnStorageMergeEnded(result);
  }

  internal override ColumnBackup CreateBackup(Backup.LiveBackupOptions options)
      => new CapturedColumnBackup<VersionedKey<TKey>, VersionedValue<TValue>>(Id,
          (global::ZoneTree.Core.ZoneTree<VersionedKey<TKey>, VersionedValue<TValue>>)Tree.Storage, options);

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

  // A view registers nothing with the column. Its operations check that the column was not
  // dropped, and a commit that used a dropped column fails.
  internal override ColumnView CreateView(bool writing, TransactionIsolationLevel isolation,
      long transactionId, long snapshotTimestamp)
  {
    var view = (ColumnView)(writing
        ? Tree.BeginTransaction(isolation, transactionId, snapshotTimestamp)
        : Tree.BeginReadOnlyTransaction(snapshotTimestamp));
    view.Column = this;
    return view;
  }

  internal override Task SyncWALAsync() => Domain.Coordinator.SyncAsync();

  Task ICoordinatedStorage.StartSync() => IsDropped ? null : Tree.StartSync();

  bool ICoordinatedStorage.OnSyncFailed(Exception error)
  {
    // A column dropped meanwhile has nothing left to make durable.
    if (IsDropped && error is global::System.ObjectDisposedException)
      return true;
    Tree.OnSyncFailed();
    return false;
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
