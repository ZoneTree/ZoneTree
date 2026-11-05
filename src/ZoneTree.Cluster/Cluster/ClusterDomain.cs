using ZoneTree.Exceptions;
using ZoneTree.Transactional;
using ArgumentOutOfRangeException = System.ArgumentOutOfRangeException;

namespace ZoneTree.Cluster;

internal sealed class ClusterDomain : IClusterDomain
{
  internal readonly ZoneTreeCluster Cluster;

  /// <summary>The catalog id of the domain, which never changes and is never reused.</summary>
  internal readonly long Id;

  readonly DomainTimestampSource Timestamps;

  public string Name { get; }
  /// <summary>The domain's column names in creation order, including columns created at run time.</summary>
  public IReadOnlyList<string> ColumnNames => Cluster.Catalog.State.ColumnNamesOf(Id);
  IZoneTreeCluster IClusterDomain.Cluster => Cluster;
  public long SnapshotTimestamp => Timestamps.SnapshotTimestamp;

  internal ClusterDomain(ZoneTreeCluster cluster, DomainRecord record, DomainTimestampSource timestamps)
  {
    Cluster = cluster;
    Id = record.Id;
    Name = record.Name;
    Timestamps = timestamps;
  }

  public ICoordinatedColumn<TKey, TValue> GetColumn<TKey, TValue>(string name)
      => Cluster.GetCoordinatedColumn<TKey, TValue>(this, name);

  public IDomainTransaction BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable)
  {
    if (isolationLevel is not TransactionIsolationLevel.Snapshot and not TransactionIsolationLevel.Serializable)
      throw new ArgumentOutOfRangeException(nameof(isolationLevel), isolationLevel, null);
    return BeginTransaction(new ClusterTransactionOptions { IsolationLevel = isolationLevel });
  }

  public IDomainTransaction BeginTransaction(ClusterTransactionOptions options)
  {
    options ??= new();
    Cluster.ThrowIfDisposed();
    TransactionExpiry.Validate(options);
    if (Cluster.IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    return new DomainTransaction(this, Timestamps.Next(), Timestamps.SnapshotTimestamp, options);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IDomainTransaction> BeginTransactionAsync(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginTransaction(isolationLevel));
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IDomainTransaction> BeginTransactionAsync(
      ClusterTransactionOptions options, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginTransaction(options));
  }

  public IDomainReadTransaction BeginReadOnlyTransaction()
  {
    Cluster.ThrowIfDisposed();
    return new DomainReadTransaction(this, Timestamps.SnapshotTimestamp);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IDomainReadTransaction> BeginReadOnlyTransactionAsync(
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginReadOnlyTransaction());
  }

  public IDomainReadTransaction BeginReadOnlyTransaction(ClusterReadTransactionOptions options)
  {
    options ??= new();
    Cluster.ThrowIfDisposed();
    TransactionExpiry.Validate(options);
    return new DomainReadTransaction(this, Timestamps.SnapshotTimestamp, options.Timeout);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IDomainReadTransaction> BeginReadOnlyTransactionAsync(
      ClusterReadTransactionOptions options, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginReadOnlyTransaction(options));
  }

  // A transaction's outcome is decided by its commit, and an in-doubt outcome only by
  // recovery. Until the commit protocol records outcomes, this process does not know them.
  public ValueTask<TransactionOutcome> GetTransactionOutcomeAsync(
      long transactionId, CancellationToken cancellationToken = default)
  {
    Cluster.ThrowIfDisposed();
    cancellationToken.ThrowIfCancellationRequested();
    return new(TransactionOutcome.Unknown);
  }

  public Task SyncWALAsync(CancellationToken cancellationToken = default)
  {
    Cluster.ThrowIfDisposed();
    cancellationToken.ThrowIfCancellationRequested();
    var sync = Task.WhenAll(Cluster.OpenColumnsOf(this).Select(column => column.SyncWALAsync()));
    return cancellationToken.CanBeCanceled ? sync.WaitAsync(cancellationToken) : sync;
  }

  internal long TransactionCommitted() => Timestamps.Commit();
}
