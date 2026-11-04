using ZoneTree.Transactional;
using ZoneTree.Backup;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ArgumentOutOfRangeException = System.ArgumentOutOfRangeException;

namespace ZoneTree.Cluster;

/// <summary>An experimental in-process cluster with transactional and raw columns.</summary>
/// <remarks>Cross-column snapshots, atomic visibility and crash recovery are not provided by this first slice.</remarks>
public sealed class ZoneTreeCluster : IZoneTreeCluster, IZoneTreeClusterMaintenance
{
  readonly Dictionary<string, Column> ColumnsByName = new(StringComparer.Ordinal);
  readonly FileStream Lease;
  Column[] Columns = [];
  IReadOnlyList<string> ColumnNames = Array.Empty<string>();
  long Clock;
  long LastCommit;
  int ActiveTransactions;
  int Disposed;
  bool readOnly;
  TimeSpan autoSyncInterval;

  internal string DataDirectory { get; }
  public ILogger Logger { get; }

  internal ZoneTreeCluster(string directory, ILogger logger, FileStream lease)
  {
    DataDirectory = directory;
    Logger = logger;
    Lease = lease;
  }

  internal void Initialize(Column[] columns, ZoneTreeClusterOptions options)
  {
    Columns = columns;
    var names = new string[columns.Length];
    for (var index = 0; index < columns.Length; ++index)
    {
      ColumnsByName.Add(columns[index].Name, columns[index]);
      names[index] = columns[index].Name;
    }
    ColumnNames = Array.AsReadOnly(names);
    IsReadOnly = options.IsReadOnly;
    AutoSyncInterval = options.AutoSyncInterval;
  }

  public IColumn<TKey, TValue> GetColumn<TKey, TValue>(string name)
  {
    ThrowIfDisposed();
    if (!ColumnsByName.TryGetValue(name, out var column))
      throw new KeyNotFoundException($"Column '{name}' does not exist.");
    return column as IColumn<TKey, TValue>
        ?? throw new InvalidOperationException($"Column '{name}' has a different kind, key type or value type.");
  }

  public IRawColumn<TKey, TValue> GetRawColumn<TKey, TValue>(string name)
  {
    ThrowIfDisposed();
    if (!ColumnsByName.TryGetValue(name, out var column))
      throw new KeyNotFoundException($"Column '{name}' does not exist.");
    return column as IRawColumn<TKey, TValue>
        ?? throw new InvalidOperationException($"Column '{name}' has a different kind, key type or value type.");
  }

  public IClusterTransaction BeginTransaction(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable)
  {
    ThrowIfDisposed();
    if (isolationLevel is not TransactionIsolationLevel.Snapshot and not TransactionIsolationLevel.Serializable)
      throw new ArgumentOutOfRangeException(nameof(isolationLevel), isolationLevel, null);
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
    return new ClusterTransaction(this, Interlocked.Increment(ref Clock),
        Volatile.Read(ref LastCommit), isolationLevel);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IClusterTransaction> BeginTransactionAsync(
      TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Serializable,
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginTransaction(isolationLevel));
  }

  public IClusterReadTransaction BeginReadOnlyTransaction()
  {
    ThrowIfDisposed();
    return new ClusterReadTransaction(this, Volatile.Read(ref LastCommit));
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The returned ValueTask transfers transaction ownership to the caller.")]
  public ValueTask<IClusterReadTransaction> BeginReadOnlyTransactionAsync(
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(BeginReadOnlyTransaction());
  }

  public Task SyncWALAsync(CancellationToken cancellationToken = default)
  {
    ThrowIfDisposed();
    cancellationToken.ThrowIfCancellationRequested();
    var tasks = new Task[Columns.Length];
    for (var index = 0; index < Columns.Length; ++index)
      tasks[index] = Columns[index].SyncWALAsync();
    var sync = Task.WhenAll(tasks);
    return cancellationToken.CanBeCanceled ? sync.WaitAsync(cancellationToken) : sync;
  }

  public TimeSpan AutoSyncInterval
  {
    get => autoSyncInterval;
    set
    {
      ThrowIfDisposed();
      ValidateSyncInterval(value);
      foreach (var column in Columns)
        column.AutoSyncInterval = value;
      autoSyncInterval = value;
    }
  }

  internal static void ValidateSyncInterval(TimeSpan interval)
  {
    if (interval != TimeSpan.Zero && (interval.TotalMilliseconds < 1 || interval.TotalMilliseconds >= uint.MaxValue))
      throw new ArgumentOutOfRangeException(nameof(interval), interval, "Invalid background sync interval.");
  }

  public bool IsReadOnly
  {
    get => Volatile.Read(ref readOnly);
    set
    {
      ThrowIfDisposed();
      foreach (var column in Columns)
        column.SetReadOnly(value);
      Volatile.Write(ref readOnly, value);
    }
  }

  public IZoneTreeClusterMaintenance Maintenance => this;
  IReadOnlyList<string> IZoneTreeClusterMaintenance.ColumnNames => ColumnNames;
  public int ActiveTransactionCount => Volatile.Read(ref ActiveTransactions);
  public long LastCommitTimestamp => Volatile.Read(ref LastCommit);
  public int AbortedTransactionCount => Columns.Sum(column => column.AbortedTransactionCount);
  public int ConflictIndexCount => Columns.Sum(column => column.ConflictIndexCount);

  internal void TransactionStarted() => Interlocked.Increment(ref ActiveTransactions);
  internal void TransactionEnded() => Interlocked.Decrement(ref ActiveTransactions);
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

  public IMaintainer CreateMaintainer()
  {
    ThrowIfDisposed();
    return new ClusterMaintainer(Columns);
  }

  public void SaveMetaData()
  {
    ThrowIfDisposed();
    foreach (var column in Columns)
      column.SaveMetaData();
  }

  public void Drop()
  {
    ThrowIfDisposed();
    EnsureNoActiveTransactions();
    try
    {
      foreach (var column in Columns)
        column.Drop();
    }
    finally
    {
      Interlocked.Exchange(ref Disposed, 1);
      Lease.Dispose();
    }
    File.Delete(Path.Combine(DataDirectory, "database.schema"));
    File.Delete(Path.Combine(DataDirectory, "database.lock"));
  }

  public void Dispose()
  {
    if (Volatile.Read(ref Disposed) != 0)
      return;
    EnsureNoActiveTransactions();
    if (Interlocked.Exchange(ref Disposed, 1) != 0)
      return;
    List<Exception> errors = null;
    foreach (var column in Columns)
    {
      try { column.Dispose(); }
      catch (Exception error) { (errors ??= []).Add(error); }
    }
    Lease.Dispose();
    if (errors != null)
      throw new AggregateException(errors);
  }

  public ValueTask DisposeAsync()
  {
    Dispose();
    return ValueTask.CompletedTask;
  }

  void EnsureNoActiveTransactions()
  {
    if (ActiveTransactionCount != 0)
      throw new InvalidOperationException("End active transactions before disposing or dropping the cluster.");
  }

  internal void ThrowIfDisposed()
  {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref Disposed) != 0, this);
  }

  public ILiveBackup CreateLiveBackup(LocalLiveBackupOptions storage, ClusterLiveBackupOptions options = null)
      => throw new NotSupportedException("Cluster-wide backup and restore are not part of the first slice.");

  public ILiveBackup CreateLiveBackup(IBackupObjectStorage storage,
      ObjectStorageLiveBackupOptions storageOptions = null, ClusterLiveBackupOptions options = null)
      => throw new NotSupportedException("Cluster-wide backup and restore are not part of the first slice.");
}
