using ZoneTree.Exceptions;
using ZoneTree.Transactional;
using ArgumentOutOfRangeException = System.ArgumentOutOfRangeException;

namespace ZoneTree.Cluster;

internal sealed class ClusterDomain : IClusterDomain
{
  internal readonly ZoneTreeCluster Cluster;

  /// <summary>The catalog id of the domain, which never changes and is never reused.</summary>
  internal readonly long Id;

  /// <summary>Coordinates the domain's transactions across its columns.</summary>
  internal DomainCoordinator Coordinator { get; }

  string name;
  volatile bool dropped;

  /// <summary>The current name of the domain. A rename changes it; the handle stays.</summary>
  public string Name => Volatile.Read(ref name);
  /// <summary>The domain's column names in creation order, including columns created at run time.</summary>
  public IReadOnlyList<string> ColumnNames => Cluster.Catalog.State.ColumnNamesOf(Id);
  IZoneTreeCluster IClusterDomain.Cluster => Cluster;
  public long SnapshotTimestamp => Coordinator.Sequencer.SnapshotTimestamp;

  internal ClusterDomain(ZoneTreeCluster cluster, DomainRecord record, DomainCoordinator coordinator)
  {
    Cluster = cluster;
    Id = record.Id;
    name = record.Name;
    Coordinator = coordinator;
    coordinator.Domain = this;
  }

  internal void Rename(string newName) => Volatile.Write(ref name, newName);

  internal void MarkDropped() => dropped = true;

  /// <summary>Throws if the cluster is disposed or the domain was dropped.</summary>
  void EnsureUsable()
  {
    Cluster.ThrowIfDisposed();
    if (dropped)
      throw new SchemaObjectDroppedException($"Domain '{Name}' was dropped.");
  }

  public ICoordinatedColumn<TKey, TValue> GetColumn<TKey, TValue>(string name)
  {
    EnsureUsable();
    return Cluster.GetCoordinatedColumn<TKey, TValue>(this, name);
  }

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
    EnsureUsable();
    TransactionExpiry.Validate(options);
    if (Cluster.IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    var writer = Coordinator.Snapshots.BeginWriter();
    try
    {
      return new DomainTransaction(this, writer, options);
    }
    catch
    {
      // A registered writer keeps the domain's conflict keys and old versions.
      Coordinator.Snapshots.EndWriter(writer);
      throw;
    }
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned Task transfers transaction ownership to the caller.")]
  public Task<IDomainTransaction> BeginTransactionAsync(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult(BeginTransaction(isolationLevel));
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned Task transfers transaction ownership to the caller.")]
  public Task<IDomainTransaction> BeginTransactionAsync(
      ClusterTransactionOptions options, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult(BeginTransaction(options));
  }

  public IDomainReadTransaction BeginReadOnlyTransaction()
  {
    EnsureUsable();
    return new DomainReadTransaction(this, Coordinator.Snapshots.JoinReader());
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned Task transfers transaction ownership to the caller.")]
  public Task<IDomainReadTransaction> BeginReadOnlyTransactionAsync(
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult(BeginReadOnlyTransaction());
  }

  public IDomainReadTransaction BeginReadOnlyTransaction(ClusterReadTransactionOptions options)
  {
    options ??= new();
    EnsureUsable();
    TransactionExpiry.Validate(options);
    var bucket = Coordinator.Snapshots.JoinReader();
    try
    {
      return new DomainReadTransaction(this, bucket, options.Timeout);
    }
    catch
    {
      bucket.Leave();
      throw;
    }
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned Task transfers transaction ownership to the caller.")]
  public Task<IDomainReadTransaction> BeginReadOnlyTransactionAsync(
      ClusterReadTransactionOptions options, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult(BeginReadOnlyTransaction(options));
  }

  // Outcomes come from the columns open in this process: their transaction entries, then
  // their commit records, until merges drop the records of durable commits.
  public Task<TransactionOutcome> GetTransactionOutcomeAsync(
      long transactionId, CancellationToken cancellationToken = default)
  {
    EnsureUsable();
    return AsyncOperations.Run(() => FindOutcome(transactionId), cancellationToken);
  }

  /// <summary>
  /// A committed column decides the outcome, then a column that has not decided yet, then a
  /// rolled back one. A column the transaction never wrote does not know it.
  /// </summary>
  TransactionOutcome FindOutcome(long transactionId)
  {
    if (transactionId <= 0 || transactionId > Coordinator.Clock.Current)
      return TransactionOutcome.Unknown;
    var outcome = TransactionOutcome.Unknown;
    foreach (var column in Cluster.OpenColumnsOf(this))
    {
      var columnOutcome = ((ICoordinatedStorage)column).GetOutcome(transactionId);
      if (columnOutcome == TransactionOutcome.Committed)
        return columnOutcome;
      if (columnOutcome == TransactionOutcome.Pending ||
          (columnOutcome == TransactionOutcome.Aborted && outcome == TransactionOutcome.Unknown))
        outcome = columnOutcome;
    }
    return outcome;
  }

  public Task SyncWALAsync(CancellationToken cancellationToken = default)
  {
    EnsureUsable();
    cancellationToken.ThrowIfCancellationRequested();
    var sync = Coordinator.SyncAsync();
    return cancellationToken.CanBeCanceled ? sync.WaitAsync(cancellationToken) : sync;
  }
}
