using ZoneTree.Exceptions;
using ZoneTree.Transactional;
using ArgumentOutOfRangeException = System.ArgumentOutOfRangeException;

namespace ZoneTree.Cluster;

internal sealed class ClusterDomain : IClusterDomain
{
  readonly Dictionary<string, Column> ColumnsByName = new(StringComparer.Ordinal);
  internal readonly ZoneTreeCluster Cluster;
  readonly Column[] Columns;
  long Clock;
  long LastCommit;

  public string Name { get; }
  public IReadOnlyList<string> ColumnNames { get; }
  IZoneTreeCluster IClusterDomain.Cluster => Cluster;
  public long LastCommitTimestamp => Volatile.Read(ref LastCommit);

  internal ClusterDomain(ZoneTreeCluster cluster, string name, Column[] columns)
  {
    Cluster = cluster;
    Name = name;
    Columns = columns;
    var names = new string[columns.Length];
    for (var index = 0; index < columns.Length; ++index)
    {
      ColumnsByName.Add(columns[index].Name, columns[index]);
      names[index] = columns[index].Name;
    }
    ColumnNames = Array.AsReadOnly(names);
  }

  public ICoordinatedColumn<TKey, TValue> GetColumn<TKey, TValue>(string name)
  {
    Cluster.ThrowIfDisposed();
    if (!ColumnsByName.TryGetValue(name, out var column))
      throw new KeyNotFoundException($"Domain '{Name}' has no column '{name}'.");
    return column as ICoordinatedColumn<TKey, TValue>
        ?? throw new InvalidOperationException($"Column '{name}' has a different key type or value type.");
  }

  public IDomainTransaction BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable)
  {
    Cluster.ThrowIfDisposed();
    if (isolationLevel is not TransactionIsolationLevel.Snapshot and not TransactionIsolationLevel.Serializable)
      throw new ArgumentOutOfRangeException(nameof(isolationLevel), isolationLevel, null);
    if (Cluster.IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    return new DomainTransaction(this, Interlocked.Increment(ref Clock),
        Volatile.Read(ref LastCommit), isolationLevel);
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

  public IDomainReadTransaction BeginReadOnlyTransaction()
  {
    Cluster.ThrowIfDisposed();
    return new DomainReadTransaction(this, Volatile.Read(ref LastCommit));
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IDomainReadTransaction> BeginReadOnlyTransactionAsync(
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginReadOnlyTransaction());
  }

  public Task SyncWALAsync(CancellationToken cancellationToken = default)
  {
    Cluster.ThrowIfDisposed();
    cancellationToken.ThrowIfCancellationRequested();
    var tasks = new Task[Columns.Length];
    for (var index = 0; index < Columns.Length; ++index)
      tasks[index] = Columns[index].SyncWALAsync();
    var sync = Task.WhenAll(tasks);
    return cancellationToken.CanBeCanceled ? sync.WaitAsync(cancellationToken) : sync;
  }

  internal long TransactionCommitted()
  {
    var timestamp = Interlocked.Increment(ref Clock);
    var current = Volatile.Read(ref LastCommit);
    while (current < timestamp)
    {
      var observed = Interlocked.CompareExchange(ref LastCommit, timestamp, current);
      if (observed == current)
        break;
      current = observed;
    }
    return timestamp;
  }
}
