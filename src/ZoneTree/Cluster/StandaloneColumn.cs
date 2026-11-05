using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal sealed class StandaloneColumn<TKey, TValue> : Column,
    IStandaloneColumn<TKey, TValue>
{
  internal readonly ITransactionalZoneTree<TKey, TValue> Tree;
  volatile bool Disposed;

  internal StandaloneColumn(ZoneTreeCluster cluster, int index, string name,
      ITransactionalZoneTree<TKey, TValue> tree) : base(cluster, index, name) => Tree = tree;

  IZoneTreeCluster IStandaloneColumn<TKey, TValue>.Cluster => Cluster;
  internal override int AbortedTransactionCount => Tree.Maintenance.AbortedTransactionCount;
  internal override int ConflictIndexCount => Tree.Maintenance.ConflictIndexCount;

  public IStandaloneTransaction<TKey, TValue> BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable)
  {
    EnsureOpen();
    return new StandaloneTransaction<TKey, TValue>(Cluster, Tree.BeginTransaction(isolationLevel));
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IStandaloneTransaction<TKey, TValue>> BeginTransactionAsync(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginTransaction(isolationLevel));
  }

  public IStandaloneReadTransaction<TKey, TValue> BeginReadOnlyTransaction()
  {
    EnsureOpen();
    return new StandaloneReadTransaction<TKey, TValue>(Cluster, Tree.BeginReadOnlyTransaction());
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IStandaloneReadTransaction<TKey, TValue>> BeginReadOnlyTransactionAsync(
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginReadOnlyTransaction());
  }

  // The outcome of an in-doubt commit is decided by recovery when the column reopens.
  public ValueTask<TransactionOutcome> GetTransactionOutcomeAsync(
      long transactionId, CancellationToken cancellationToken = default)
  {
    EnsureOpen();
    cancellationToken.ThrowIfCancellationRequested();
    return new(TransactionOutcome.Unknown);
  }

  public Task SyncWALAsync(CancellationToken cancellationToken = default)
  {
    EnsureOpen();
    return Tree.SyncWALAsync(cancellationToken);
  }

  internal override Task SyncWALAsync() => SyncWALAsync(default);

  internal override void SetReadOnly(bool value)
  {
    EnsureOpen();
    Tree.IsReadOnly = value;
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
      => throw new InvalidOperationException("Standalone columns cannot participate in domain transactions.");

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

  public override void Dispose()
  {
    if (Disposed)
      return;
    Disposed = true;
    Tree.Dispose();
  }

  void EnsureOpen() => ObjectDisposedException.ThrowIf(Disposed, this);
}
