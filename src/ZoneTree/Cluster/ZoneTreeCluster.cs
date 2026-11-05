using System.Collections.Concurrent;
using ZoneTree.Transactional;
using ZoneTree.Backup;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ArgumentOutOfRangeException = System.ArgumentOutOfRangeException;

namespace ZoneTree.Cluster;

/// <summary>An in-process cluster with coordinated, standalone and raw columns.</summary>
/// <remarks>Snapshots, visibility and crash recovery across the columns of a domain are not atomic yet.</remarks>
public sealed class ZoneTreeCluster : IZoneTreeCluster, IZoneTreeClusterMaintenance
{
  readonly Dictionary<string, Column> ColumnsByName = new(StringComparer.Ordinal);
  readonly Dictionary<string, ClusterDomain> DomainsByName = new(StringComparer.Ordinal);
  readonly FileStream Lease;
  Column[] Columns = [];
  IReadOnlyList<string> ColumnNames = Array.Empty<string>();
  IReadOnlyList<string> DomainNames = Array.Empty<string>();
  // The transactions that disposal rolls back if they are still active.
  readonly ConcurrentDictionary<DomainReadTransaction, byte> OpenTransactions = new();
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

  /// <param name="columns">Every column of the cluster, in schema declaration order.</param>
  /// <param name="domains">The domain names with the columns of each domain.</param>
  /// <param name="options">The cluster options.</param>
  internal void Initialize(Column[] columns,
      IReadOnlyList<(string Name, Column[] Columns)> domains, ZoneTreeClusterOptions options)
  {
    Columns = columns;
    var names = new string[columns.Length];
    for (var index = 0; index < columns.Length; ++index)
    {
      ColumnsByName.Add(columns[index].Name, columns[index]);
      names[index] = columns[index].Name;
    }
    ColumnNames = Array.AsReadOnly(names);
    var domainNames = new string[domains.Count];
    for (var index = 0; index < domains.Count; ++index)
    {
      var domain = new ClusterDomain(this, domains[index].Name, domains[index].Columns);
      foreach (var column in domains[index].Columns)
        column.Domain = domain;
      DomainsByName.Add(domain.Name, domain);
      domainNames[index] = domain.Name;
    }
    DomainNames = Array.AsReadOnly(domainNames);
    IsReadOnly = options.IsReadOnly;
    AutoSyncInterval = options.AutoSyncInterval;
  }

  public IClusterDomain GetDomain(string name)
  {
    ThrowIfDisposed();
    return DomainsByName.TryGetValue(name, out var domain)
        ? domain
        : throw new KeyNotFoundException($"Domain '{name}' does not exist.");
  }

  public IRawColumn<TKey, TValue> GetRawColumn<TKey, TValue>(string name)
  {
    ThrowIfDisposed();
    if (!ColumnsByName.TryGetValue(name, out var column))
      throw new KeyNotFoundException($"Column '{name}' does not exist.");
    return column as IRawColumn<TKey, TValue>
        ?? throw new InvalidOperationException($"Column '{name}' has a different kind, key type or value type.");
  }

  public IStandaloneColumn<TKey, TValue> GetStandaloneColumn<TKey, TValue>(string name)
  {
    ThrowIfDisposed();
    if (!ColumnsByName.TryGetValue(name, out var column))
      throw new KeyNotFoundException($"Column '{name}' does not exist.");
    return column as IStandaloneColumn<TKey, TValue>
        ?? throw new InvalidOperationException($"Column '{name}' has a different kind, key type or value type.");
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
  IReadOnlyList<string> IZoneTreeClusterMaintenance.DomainNames => DomainNames;
  public int ActiveTransactionCount => OpenTransactions.Count;
  public int AbortedTransactionCount => Columns.Sum(column => column.AbortedTransactionCount);
  public int ConflictIndexCount => Columns.Sum(column => column.ConflictIndexCount);

  internal void TransactionStarted(DomainReadTransaction transaction) => OpenTransactions.TryAdd(transaction, 0);
  internal void TransactionEnded(DomainReadTransaction transaction) => OpenTransactions.TryRemove(transaction, out _);

  void RollBackOpenTransactions(ref List<Exception> errors)
  {
    foreach (var transaction in OpenTransactions.Keys)
    {
      try { transaction.RollbackOnClose(); }
      catch (Exception error) { (errors ??= []).Add(error); }
    }
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
    if (Interlocked.Exchange(ref Disposed, 1) != 0)
      ObjectDisposedException.ThrowIf(true, this);
    // The dropped data goes away with the transactions, so a failed rollback is only logged.
    List<Exception> errors = null;
    RollBackOpenTransactions(ref errors);
    errors?.ForEach(Logger.LogError);
    try
    {
      foreach (var column in Columns)
        column.Drop();
    }
    finally
    {
      Lease.Dispose();
    }
    File.Delete(Path.Combine(DataDirectory, "database.schema"));
    File.Delete(Path.Combine(DataDirectory, "database.lock"));
  }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref Disposed, 1) != 0)
      return;
    List<Exception> errors = null;
    RollBackOpenTransactions(ref errors);
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

  internal void ThrowIfDisposed()
  {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref Disposed) != 0, this);
  }

  public ILiveBackup CreateLiveBackup(LocalLiveBackupOptions storage, ClusterLiveBackupOptions options = null)
      => throw new NotSupportedException("Cluster-wide backup and restore are not supported yet.");

  public ILiveBackup CreateLiveBackup(IBackupObjectStorage storage,
      ObjectStorageLiveBackupOptions storageOptions = null, ClusterLiveBackupOptions options = null)
      => throw new NotSupportedException("Cluster-wide backup and restore are not supported yet.");
}
