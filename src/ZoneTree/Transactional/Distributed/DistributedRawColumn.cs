namespace ZoneTree.Transactional.Distributed;

internal sealed class DistributedRawColumn<TKey, TValue> : DistributedDatabaseColumn,
    IDistributedRawColumn<TKey, TValue>
{
  readonly IZoneTree<TKey, TValue> Tree;
  readonly object AutoSyncLock = new();
  volatile bool Disposed;
  TimeSpan autoSyncInterval;
  CancellationTokenSource AutoSyncCancellation;
  Task AutoSyncTask;

  internal DistributedRawColumn(DistributedDatabase database, int index, string name,
      IZoneTree<TKey, TValue> tree) : base(database, index, name) => Tree = tree;

  IDistributedDatabase IDistributedRawColumn<TKey, TValue>.Database => Database;
  public IZoneTreeMaintenance<TKey, TValue> Maintenance => Tree.Maintenance;
  internal override int AbortedTransactionCount => 0;
  internal override int ConflictIndexCount => 0;
  internal override void SetReadOnly(bool value) => Tree.IsReadOnly = value;

  public bool ContainsKey(in TKey key)
  {
    EnsureOpen();
    return Tree.ContainsKey(in key);
  }

  public bool TryGet(in TKey key, out TValue value)
  {
    EnsureOpen();
    return Tree.TryGet(in key, out value);
  }

  public void Upsert(in TKey key, in TValue value)
  {
    EnsureOpen();
    Tree.Upsert(in key, in value);
  }

  public void Delete(in TKey key)
  {
    EnsureOpen();
    Tree.ForceDelete(in key);
  }

  public ValueTask<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return new(ContainsKey(in key));
  }

  public ValueTask<DistributedReadResult<TValue>> TryGetAsync(TKey key, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var found = TryGet(in key, out var value);
    return new(new DistributedReadResult<TValue>(found, value));
  }

  public ValueTask UpsertAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    Upsert(in key, in value);
    return ValueTask.CompletedTask;
  }

  public ValueTask DeleteAsync(TKey key, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    Delete(in key);
    return ValueTask.CompletedTask;
  }

  public IEnumerable<KeyValuePair<TKey, TValue>> Range(in TKey from, in TKey to, bool reverse = false)
  {
    EnsureOpen();
    return EnumerateRange(from, to, reverse);
  }

  IEnumerable<KeyValuePair<TKey, TValue>> EnumerateRange(TKey from, TKey to, bool reverse)
  {
    var comparer = Tree.Comparer;
    if (comparer.Compare(in from, in to) > 0)
      yield break;
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
      => new DistributedAsyncRange<TKey, TValue>(Range(in from, in to, reverse), cancellationToken);

  public IZoneTreeIterator<TKey, TValue> CreateIterator(IteratorOptions options = null)
  {
    EnsureOpen();
    return options == null ? Tree.CreateIterator() : Tree.CreateIterator(options);
  }

  public IZoneTreeIterator<TKey, TValue> CreateReverseIterator(IteratorOptions options = null)
  {
    EnsureOpen();
    return options == null ? Tree.CreateReverseIterator() : Tree.CreateReverseIterator(options);
  }

  public IDistributedAsyncIterator<TKey, TValue> CreateAsyncIterator(IteratorOptions options = null)
      => new DistributedAsyncRawIterator<TKey, TValue>(CreateIterator(options));

  public IDistributedAsyncIterator<TKey, TValue> CreateAsyncReverseIterator(IteratorOptions options = null)
      => new DistributedAsyncRawIterator<TKey, TValue>(CreateReverseIterator(options));

  public Task SyncWALAsync(CancellationToken cancellationToken = default)
  {
    EnsureOpen();
    return Tree.SyncWALAsync(cancellationToken);
  }

  internal override Task SyncWALAsync() => SyncWALAsync(default);
  internal override void SaveMetaData() => Tree.Maintenance.SaveMetaData();
  internal override DistributedDatabaseColumnView CreateView(bool writing, TransactionIsolationLevel isolation)
      => throw new InvalidOperationException("Raw columns cannot participate in database transactions.");

  internal override TimeSpan AutoSyncInterval
  {
    get => autoSyncInterval;
    set
    {
      DistributedDatabase.ValidateSyncInterval(value);
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
    Tree.Dispose();
  }

  internal override void Drop()
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
}
