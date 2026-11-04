namespace ZoneTree.Transactional.Distributed;

internal abstract class DistributedDatabaseColumn : IDisposable
{
  internal readonly DistributedDatabase Database;
  internal readonly int Index;
  // TODO: Revisit this extra gate when column validation and preparation can be atomic.
  // It currently keeps another database transaction from committing to a column between
  // multi-column conflict preflight and commit startup, which could otherwise cause a
  // late conflict after an earlier column has committed. Single-column commits must
  // respect it too. Each transactional column owns a separate MVCC tree; the locks
  // inside those trees do not cover this multi-column preflight and commit sequence.
  internal readonly object CommitGate = new();
  public string Name { get; }

  protected DistributedDatabaseColumn(DistributedDatabase database, int index, string name)
  {
    Database = database;
    Index = index;
    Name = name;
  }

  internal abstract DistributedDatabaseColumnView CreateView(bool writing, TransactionIsolationLevel isolation);
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

internal sealed class DistributedDatabaseColumn<TKey, TValue> : DistributedDatabaseColumn,
    IDistributedColumn<TKey, TValue>
{
  internal readonly DistributedMvccZoneTree<TKey, TValue> Tree;

  internal DistributedDatabaseColumn(DistributedDatabase database, int index, string name,
      DistributedMvccZoneTree<TKey, TValue> tree) : base(database, index, name) => Tree = tree;

  IDistributedDatabase IDistributedColumn<TKey, TValue>.Database => Database;
  public ITransactionalZoneTreeMaintenance<TKey, TValue> Maintenance => Tree.Maintenance;
  internal override int AbortedTransactionCount => Tree.AbortedTransactionCount;
  internal override int ConflictIndexCount => Tree.ConflictIndexCount;
  internal override TimeSpan AutoSyncInterval
  {
    get => Tree.AutoSyncInterval;
    set => Tree.AutoSyncInterval = value;
  }

  internal override DistributedDatabaseColumnView CreateView(bool writing, TransactionIsolationLevel isolation)
  {
    var view = (DistributedDatabaseColumnView)(writing
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
