using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal sealed class StandaloneColumn<TKey, TValue> : Column,
    IStandaloneColumn<TKey, TValue>
{
  internal readonly ITransactionalZoneTree<TKey, TValue> Tree;
  volatile bool Disposed;

  internal StandaloneColumn(ZoneTreeCluster cluster, long id, string name,
      ITransactionalZoneTree<TKey, TValue> tree) : base(cluster, id, name) => Tree = tree;

  IZoneTreeCluster IStandaloneColumn<TKey, TValue>.Cluster => Cluster;
  internal override int AbortedTransactionCount => Tree.Maintenance.AbortedTransactionCount;
  internal override int ConflictIndexCount => Tree.Maintenance.ConflictIndexCount;

  // The MVCC tree counts its own writing and read-only transactions.
  internal override int ActiveTransactionCount => Tree.Maintenance.ActiveTransactionCount;

  public IStandaloneTransaction<TKey, TValue> BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable)
      => Begin(isolationLevel, TimeSpan.Zero);

  public IStandaloneTransaction<TKey, TValue> BeginTransaction(ClusterTransactionOptions options)
  {
    options ??= new();
    TransactionExpiry.Validate(options);
    return Begin(options.IsolationLevel, options.Timeout);
  }

  // A transaction registers nothing with the column: its operations check that the column
  // was not dropped, and dropping the storage closes the files its iterators read. Only a
  // writing transaction registers with the cluster, which rolls it back when it closes.
  StandaloneTransaction<TKey, TValue> Begin(TransactionIsolationLevel isolationLevel, TimeSpan timeout)
  {
    EnsureOpen();
    return new StandaloneTransaction<TKey, TValue>(this, Tree.BeginTransaction(isolationLevel), timeout);
  }

  StandaloneReadTransaction<TKey, TValue> BeginRead(TimeSpan timeout)
  {
    EnsureOpen();
    return new StandaloneReadTransaction<TKey, TValue>(this, Tree.BeginReadOnlyTransaction(), timeout);
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

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IStandaloneTransaction<TKey, TValue>> BeginTransactionAsync(
      ClusterTransactionOptions options, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginTransaction(options));
  }

  public IStandaloneReadTransaction<TKey, TValue> BeginReadOnlyTransaction() => BeginRead(TimeSpan.Zero);

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IStandaloneReadTransaction<TKey, TValue>> BeginReadOnlyTransactionAsync(
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginReadOnlyTransaction());
  }

  public IStandaloneReadTransaction<TKey, TValue> BeginReadOnlyTransaction(ClusterReadTransactionOptions options)
  {
    options ??= new();
    TransactionExpiry.Validate(options);
    return BeginRead(options.Timeout);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IStandaloneReadTransaction<TKey, TValue>> BeginReadOnlyTransactionAsync(
      ClusterReadTransactionOptions options, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginReadOnlyTransaction(options));
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

  internal override ColumnView CreateView(bool writing, TransactionIsolationLevel isolation,
      long transactionId, long snapshotTimestamp)
      => throw new InvalidOperationException("Standalone columns cannot participate in domain transactions.");

  internal override void SaveMetaData() => Tree.Maintenance.SaveMetaData();

  public override IMaintainer CreateMaintainer()
  {
    EnsureOpen();
    return Tree.CreateMaintainer();
  }

  protected override void DropTree()
  {
    Disposed = true;
    Tree.Maintenance.Drop();
  }

  public override void Dispose()
  {
    if (Disposed)
      return;
    Disposed = true;
    StopOperations();
    Tree.Dispose();
  }

  void EnsureOpen()
  {
    ThrowIfDropped();
    ObjectDisposedException.ThrowIf(Disposed, this);
  }
}
