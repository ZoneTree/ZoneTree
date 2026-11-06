using ZoneTree.Backup;
using ZoneTree.Logger;

namespace ZoneTree.Cluster;

/// <summary>
/// Live backups of a whole cluster while it serves reads and writes: the catalog, and every
/// column, through the engine's live backup of each column's storage.
/// </summary>
/// <remarks>
/// <para>
/// A generation is consistent per domain. Under the schema lock it pins each domain's cut, the
/// latest snapshot with the sequence number of its newest commit, and captures every
/// coordinated column's storage while the cut is pinned, so each capture holds every commit
/// of the cut and every version its snapshot reads. Restore keeps the commits at or below the
/// cut in every column and rolls back the rest. Raw and standalone columns are captured on
/// their own, as the engine backs up a plain or an MVCC tree.
/// </para>
/// <para>
/// The columns upload concurrently, each into its own store, and publish their generations
/// only once all of them uploaded. The cluster manifest follows. Drops wait for a running
/// generation, so no column's storage goes while it is being read.
/// </para>
/// </remarks>
internal sealed class ClusterLiveBackup : ILiveBackup
{
  readonly ZoneTreeCluster Cluster;
  readonly ClusterBackupLocation Location;
  readonly ClusterLiveBackupOptions Options;
  readonly object SyncRoot = new();
  TaskCompletionSource<bool> StateChanged = CreateTaskSource();
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
      Justification = "Dispose disposes it after the scheduler stopped.")]
  CancellationTokenSource SchedulerCancellation;
  int RunningSchedulerCount;
  LiveBackupStatus Status = new(false, LiveBackupAttemptState.None, 0, 0, 0, 0, 0, string.Empty);
  bool IsDisposed;

  internal ClusterLiveBackup(ZoneTreeCluster cluster, ClusterBackupLocation location,
      ClusterLiveBackupOptions options)
  {
    Cluster = cluster;
    Location = location;
    Options = options ?? new ClusterLiveBackupOptions();
  }

  public long CurrentGenerationId
  {
    get
    {
      lock (SyncRoot)
        return Status.LastSuccessfulGenerationId;
    }
  }

  public LiveBackupStatus GetStatus()
  {
    lock (SyncRoot)
      return Status;
  }

  public void Start()
  {
    lock (SyncRoot)
    {
      ObjectDisposedException.ThrowIf(IsDisposed, this);
      if (Status.Started)
        return;
      Status = Status with { Started = true };
      StartScheduler();
      SignalStateChanged();
    }
  }

  public void Stop()
  {
    lock (SyncRoot)
    {
      Status = Status with { Started = false };
      SchedulerCancellation?.Cancel();
      SchedulerCancellation = null;
      SignalStateChanged();
    }
  }

  public void CreateGeneration()
      => Task.Run(() => CreateGenerationAsync()).GetAwaiter().GetResult();

  public async Task CreateGenerationAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    BeginGeneration(skipIfBusy: false, requireStarted: false);
    await RunAcceptedGenerationAsync(logFailure: false, cancellationToken).ConfigureAwait(false);
  }

  public void WaitForLiveBackup() => Task.Run(() => WaitForLiveBackupAsync()).GetAwaiter().GetResult();

  public async Task WaitForLiveBackupAsync(CancellationToken cancellationToken = default)
  {
    while (true)
    {
      Task wait;
      lock (SyncRoot)
      {
        if (!Status.Started && Status.AttemptState != LiveBackupAttemptState.Running &&
            RunningSchedulerCount == 0)
          return;
        wait = StateChanged.Task;
      }
      await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>Called after a merge of any column's storage succeeded.</summary>
  internal void OnColumnMergeSucceeded()
  {
    if (Options.BackupAfterMerge)
      StartBackgroundGeneration();
  }

  void StartScheduler()
  {
    if (Options.Schedule == null || Options.Schedule.Kind == LiveBackupScheduleKind.None)
      return;
    var cancellation = new CancellationTokenSource();
    SchedulerCancellation = cancellation;
    ++RunningSchedulerCount;
    _ = RunSchedulerAsync(cancellation);
  }

  async Task RunSchedulerAsync(CancellationTokenSource cancellation)
  {
    var token = cancellation.Token;
    try
    {
      while (true)
      {
        var next = Options.Schedule.GetNextUtc(DateTime.UtcNow);
        while (true)
        {
          var delay = next - DateTime.UtcNow;
          if (delay <= TimeSpan.Zero)
            break;
          await Task.Delay(delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        StartBackgroundGeneration();
      }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    catch (Exception error)
    {
      Cluster.Logger.LogError(error);
    }
    finally
    {
      lock (SyncRoot)
      {
        --RunningSchedulerCount;
        if (ReferenceEquals(SchedulerCancellation, cancellation))
          SchedulerCancellation = null;
        cancellation.Dispose();
        SignalStateChanged();
      }
    }
  }

  void StartBackgroundGeneration()
  {
    if (BeginGeneration(skipIfBusy: true, requireStarted: true))
      _ = Task.Run(() => RunAcceptedGenerationAsync(logFailure: true, CancellationToken.None));
  }

  bool BeginGeneration(bool skipIfBusy, bool requireStarted)
  {
    lock (SyncRoot)
    {
      if (requireStarted && !Status.Started)
        return false;
      if (IsDisposed)
      {
        if (skipIfBusy)
          return false;
        throw new System.ObjectDisposedException(GetType().Name);
      }
      if (Status.AttemptState == LiveBackupAttemptState.Running)
      {
        if (skipIfBusy)
          return false;
        throw new InvalidOperationException("A live backup generation is already running.");
      }
      Status = Status with
      {
        AttemptState = LiveBackupAttemptState.Running,
        AttemptGenerationId = 0,
        LastStartedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        LastFinishedAtUnixSeconds = 0,
        LastError = string.Empty
      };
      SignalStateChanged();
      return true;
    }
  }

  async Task RunAcceptedGenerationAsync(bool logFailure, CancellationToken cancellationToken)
  {
    var outcome = LiveBackupAttemptState.Failed;
    var error = string.Empty;
    long generationId = 0;
    try
    {
      generationId = await BackupGenerationAsync(cancellationToken).ConfigureAwait(false);
      outcome = LiveBackupAttemptState.Succeeded;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      outcome = LiveBackupAttemptState.Cancelled;
      throw;
    }
    catch (Exception exception)
    {
      var cause = exception.GetBaseException();
      error = ReferenceEquals(cause, exception) ? exception.Message : $"{exception.Message} {cause.Message}";
      if (logFailure)
        Cluster.Logger.LogError(exception);
      else
        throw;
    }
    finally
    {
      lock (SyncRoot)
      {
        var finished = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var succeeded = outcome == LiveBackupAttemptState.Succeeded;
        Status = Status with
        {
          AttemptState = outcome,
          LastFinishedAtUnixSeconds = finished,
          LastError = error,
          LastSuccessfulGenerationId = succeeded ? generationId : Status.LastSuccessfulGenerationId,
          LastCompletedAtUnixSeconds = succeeded ? finished : Status.LastCompletedAtUnixSeconds
        };
        SignalStateChanged();
      }
    }
  }

  async Task<long> BackupGenerationAsync(CancellationToken cancellationToken)
  {
    using var generation = Cluster.BeginBackupGeneration(CreateColumnOptions);
    var existing = await Location.ListManifestsAsync(cancellationToken).ConfigureAwait(false);
    var generationId = (existing.Count == 0 ? 0 : existing.Max()) + 1;
    lock (SyncRoot)
      Status = Status with { AttemptGenerationId = generationId };

    var columns = await Task.WhenAll(generation.Backups.Select(async backup =>
    {
      try
      {
        return new ClusterBackupColumn(backup.ColumnId,
            await backup.RunAsync(cancellationToken).ConfigureAwait(false));
      }
      catch
      {
        generation.Publishing.Fail();
        throw;
      }
    })).ConfigureAwait(false);

    var manifest = new ClusterBackupManifest(generationId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        generation.Catalog.Cluster, generation.Catalog.Domains, generation.Catalog.Columns,
        generation.Cuts, columns);
    await Location.WriteManifestAsync(generationId, manifest.Encode(), cancellationToken).ConfigureAwait(false);
    await ApplyRetentionAsync(generationId, cancellationToken).ConfigureAwait(false);
    return generationId;
  }

  /// <summary>The engine backup options of one column of a generation.</summary>
  LiveBackupOptions CreateColumnOptions(Column column, int columnCount, ClusterBackupPublishing publishing)
      => new()
      {
        Store = publishing.Wrap(Location.CreateColumnStore(column.Id).Store),
        BackupAfterMerge = false,
        Schedule = LiveBackupSchedule.None,
        IncludeInMemoryRecords = true,
        InMemoryMode = LiveBackupInMemoryMode.Snapshot,
        RecordBatchCompression = Options.RecordBatchCompression,
        // The columns upload concurrently and share the cluster's limit.
        MaxConcurrentFileTransfers = Math.Max(1, Options.MaxConcurrentFileTransfers / Math.Max(1, columnCount))
      };

  /// <summary>Keeps the newest manifests; the column stores keep their own generations.</summary>
  async Task ApplyRetentionAsync(long newest, CancellationToken cancellationToken)
  {
    if (Location.KeepLastGenerations is not { } keep)
      return;
    var ids = await Location.ListManifestsAsync(cancellationToken).ConfigureAwait(false);
    foreach (var id in ids.Where(id => id <= newest).OrderByDescending(id => id).Skip(keep))
      await Location.DeleteManifestAsync(id, cancellationToken).ConfigureAwait(false);
  }

  void SignalStateChanged()
  {
    var completion = StateChanged;
    StateChanged = CreateTaskSource();
    completion.TrySetResult(true);
  }

  static TaskCompletionSource<bool> CreateTaskSource()
      => new(TaskCreationOptions.RunContinuationsAsynchronously);

  public void Dispose()
  {
    lock (SyncRoot)
    {
      if (IsDisposed)
        return;
      IsDisposed = true;
    }
    Stop();
    WaitForLiveBackup();
    SchedulerCancellation?.Dispose();
    SchedulerCancellation = null;
    Cluster.RemoveLiveBackup(this);
  }
}
