using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal sealed class RawColumn<TKey, TValue> : Column,
    IRawColumn<TKey, TValue>
{
  readonly IZoneTree<TKey, TValue> Tree;
  readonly object AutoSyncLock = new();
  volatile bool Disposed;
  TimeSpan autoSyncInterval;
  CancellationTokenSource AutoSyncCancellation;
  Task AutoSyncTask;

  internal RawColumn(ZoneTreeCluster cluster, long id, string name,
      IZoneTree<TKey, TValue> tree) : base(cluster, id, name) => Tree = tree;

  IZoneTreeCluster IRawColumn<TKey, TValue>.Cluster => Cluster;
  public IZoneTreeMaintenance<TKey, TValue> Maintenance => Tree.Maintenance;
  internal override int AbortedTransactionCount => 0;
  internal override int ConflictIndexCount => 0;
  internal override void SetReadOnly(bool value) => Tree.IsReadOnly = value;

  public bool ContainsKey(in TKey key)
  {
    EnsureUsable();
    return Tree.ContainsKey(in key);
  }

  public bool TryGet(in TKey key, out TValue value)
  {
    EnsureUsable();
    return Tree.TryGet(in key, out value);
  }

  public void Upsert(in TKey key, in TValue value)
  {
    EnsureUsable();
    Tree.Upsert(in key, in value);
  }

  public void Delete(in TKey key)
  {
    EnsureUsable();
    Tree.ForceDelete(in key);
  }

  // The tree is thread-safe, so the async operations run on the thread pool without
  // an order between them, and the columns of a cluster can be written in parallel.
  public ValueTask<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => ContainsKey(in key), cancellationToken);

  public ValueTask<ReadResult<TValue>> TryGetAsync(TKey key, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => new ReadResult<TValue>(TryGet(in key, out var value), value), cancellationToken);

  public ValueTask UpsertAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => Upsert(in key, in value), cancellationToken);

  public ValueTask DeleteAsync(TKey key, CancellationToken cancellationToken = default)
      => AsyncOperations.Run(() => Delete(in key), cancellationToken);

  public IEnumerable<KeyValuePair<TKey, TValue>> Range(in TKey from, in TKey to, bool reverse = false)
  {
    EnsureUsable();
    return EnumerateRange(from, to, reverse);
  }

  IEnumerable<KeyValuePair<TKey, TValue>> EnumerateRange(TKey from, TKey to, bool reverse)
  {
    var comparer = Tree.Comparer;
    if (comparer.Compare(in from, in to) > 0)
      yield break;
    // The column's iterator reports a drop, which also disposes it.
    using var iterator = reverse ? CreateReverseIterator() : CreateIterator();
    iterator.Seek(reverse ? to : from);
    while (iterator.Next())
    {
      var key = iterator.CurrentKey;
      if (reverse ? comparer.Compare(in key, in from) < 0 : comparer.Compare(in key, in to) > 0)
        yield break;
      yield return iterator.Current;
    }
  }

  public IAsyncEnumerable<KeyValuePair<TKey, TValue>> RangeAsync(TKey from, TKey to,
      bool reverse = false, CancellationToken cancellationToken = default)
      => new AsyncRange<TKey, TValue>(Range(in from, in to, reverse), cancellationToken);

  public ITransactionIterator<TKey, TValue> CreateIterator(TransactionIteratorOptions options = null)
  {
    BeginOperation();
    try
    {
      return new RawColumnIterator<TKey, TValue>(this,
          options == null ? Tree.CreateIterator() : Tree.CreateIterator(ToStorageOptions(options)));
    }
    finally { EndOperation(); }
  }

  public ITransactionIterator<TKey, TValue> CreateReverseIterator(TransactionIteratorOptions options = null)
  {
    BeginOperation();
    try
    {
      return new RawColumnIterator<TKey, TValue>(this,
          options == null ? Tree.CreateReverseIterator() : Tree.CreateReverseIterator(ToStorageOptions(options)));
    }
    finally { EndOperation(); }
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The async iterator owns the iterator and disposes it.")]
  public IAsyncTransactionIterator<TKey, TValue> CreateAsyncIterator(TransactionIteratorOptions options = null)
      => new AsyncTransactionIterator<TKey, TValue>(CreateIterator(options));

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
      Justification = "The async iterator owns the iterator and disposes it.")]
  public IAsyncTransactionIterator<TKey, TValue> CreateAsyncReverseIterator(TransactionIteratorOptions options = null)
      => new AsyncTransactionIterator<TKey, TValue>(CreateReverseIterator(options));

  static IteratorOptions ToStorageOptions(TransactionIteratorOptions options) => new()
  {
    ContributeToTheBlockCache = options.ContributeToTheBlockCache,
    DiskSegmentPrefetchSize = options.DiskSegmentPrefetchSize
  };

  public Task SyncWALAsync(CancellationToken cancellationToken = default)
  {
    EnsureUsable();
    return Tree.SyncWALAsync(cancellationToken);
  }

  internal override Task SyncWALAsync() => SyncWALAsync(default);
  internal override void SaveMetaData() => Tree.Maintenance.SaveMetaData();
  internal override ColumnView CreateView(bool writing, TransactionIsolationLevel isolation,
      long transactionId, long snapshotTimestamp)
      => throw new InvalidOperationException("Raw columns cannot participate in cluster transactions.");

  internal override TimeSpan AutoSyncInterval
  {
    get => autoSyncInterval;
    set
    {
      ZoneTreeCluster.ValidateSyncInterval(value);
      lock (AutoSyncLock)
      {
        EnsureOpen();
        StopAutoSync();
        autoSyncInterval = value;
        if (value != TimeSpan.Zero)
        {
          AutoSyncCancellation = new();
          var token = AutoSyncCancellation.Token;
          AutoSyncTask = Task.Run(() => RunAutoSyncAsync(value, token));
        }
      }
    }
  }

  async Task RunAutoSyncAsync(TimeSpan interval, CancellationToken cancellationToken)
  {
    using var timer = new PeriodicTimer(interval);
    try
    {
      while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
      {
        try { await Tree.SyncWALAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception error) { Tree.Logger.LogError(error); }
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
  }

  void StopAutoSync()
  {
    if (AutoSyncCancellation == null)
      return;
    AutoSyncCancellation.Cancel();
    try { AutoSyncTask.GetAwaiter().GetResult(); }
    finally
    {
      AutoSyncCancellation.Dispose();
      AutoSyncCancellation = null;
      AutoSyncTask = null;
    }
  }

  public override IMaintainer CreateMaintainer()
  {
    EnsureOpen();
    return Tree.CreateMaintainer();
  }

  public override void Dispose()
  {
    lock (AutoSyncLock)
    {
      if (Disposed)
        return;
      Disposed = true;
      StopAutoSync();
    }
    StopOperations();
    Tree.Dispose();
  }

  protected override void DropTree()
  {
    lock (AutoSyncLock)
    {
      EnsureOpen();
      Disposed = true;
      StopAutoSync();
    }
    Tree.Maintenance.Drop();
  }

  void EnsureOpen() => ObjectDisposedException.ThrowIf(Disposed, this);

  // Reads and writes only check the flags. An operation that runs while the column closes
  // fails like one on a disposed ZoneTree; the engine serializes its own close.
  void EnsureUsable()
  {
    ThrowIfDropped();
    EnsureOpen();
  }
}
