using ZoneTree.AbstractFileStream;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Segments.Disk;
using ZoneTree.Segments.NullDisk;

namespace ZoneTree.Backup;

/// <summary>
/// Coordinates live backup of immutable ZoneTree disk segments and in-memory
/// records while the source tree remains open for reads and writes.
/// </summary>
public sealed class LiveBackup<TKey, TValue> : ILiveBackup
{
  readonly IZoneTree<TKey, TValue> ZoneTree;

  readonly IZoneTreeMaintenance<TKey, TValue> Maintenance;

  readonly Core.ZoneTree<TKey, TValue> CoreZoneTree;

  readonly LiveBackupOptions Options;

  readonly ZoneTreeOptions<TKey, TValue> ZoneTreeOptions;

  readonly object SyncRoot = new();

  readonly IFileStreamProvider SourceDataFileStreamProvider;

  readonly ILiveBackupStore Store;

  readonly Func<Core.ZoneTree<TKey, TValue>.BackupSegmentCollection> Capture;

  TaskCompletionSource<bool> StateChanged = CreateTaskSource();

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "SchedulerCancellation is dispsed correctly.")]
  CancellationTokenSource SchedulerCancellation;

  int RunningSchedulerCount;

  LiveBackupStatus Status = new(false, LiveBackupAttemptState.None, 0, 0, 0, 0, 0, string.Empty);

  bool IsDisposed;

  public LiveBackup(
      IZoneTree<TKey, TValue> zoneTree,
      LiveBackupOptions options)
      : this(zoneTree, options, null)
  {
  }

  internal LiveBackup(
      IZoneTree<TKey, TValue> zoneTree,
      LiveBackupOptions options,
      Func<Core.ZoneTree<TKey, TValue>.BackupSegmentCollection> capture)
  {
    ZoneTree = zoneTree ?? throw new ArgumentNullException(nameof(zoneTree));
    CoreZoneTree = zoneTree as Core.ZoneTree<TKey, TValue> ??
        throw new NotSupportedException(
            "Live backup requires the built-in ZoneTree implementation.");

    Options = options ?? throw new ArgumentNullException(nameof(options));
    Options.Normalize();

    if (Options.Store == null)
      throw new ArgumentException(
          "Live backup store is required.",
          nameof(options));

    Maintenance = zoneTree.Maintenance;
    ZoneTreeOptions = Maintenance.CloneOptions();
    SourceDataFileStreamProvider =
        ZoneTreeOptions.RandomAccessDeviceManager.FileStreamProvider;
    Store = options.Store;
    Capture = capture ?? (() => CoreZoneTree.CollectBackupSegments(
        Options.InMemoryMode == LiveBackupInMemoryMode.Snapshot, Options.IncludeInMemoryRecords));
    Maintenance.OnZoneTreeIsDisposing += OnZoneTreeIsDisposing;
  }

  void OnZoneTreeIsDisposing(IZoneTreeMaintenance<TKey, TValue> zoneTree)
  {
    Dispose();
  }

  public long CurrentGenerationId
  {
    get
    {
      lock (SyncRoot)
        return Status.LastSuccessfulGenerationId;
    }
  }

  /// <summary>Returns a consistent in-memory status without storage I/O, including after disposal.</summary>
  public LiveBackupStatus GetStatus()
  {
    lock (SyncRoot)
      return Status;
  }

  /// <summary>
  /// Starts live backup by attaching merge-triggered backup handling and
  /// starting the configured scheduler.
  /// </summary>
  public void Start()
  {
    lock (SyncRoot)
    {
      ThrowIfDisposed();

      if (Status.Started)
        return;

      Status = Status with { Started = true };
      Maintenance.OnMergeOperationEnded += OnMergeOperationEnded;
      StartScheduler();
      SignalStateChanged();
    }
  }

  /// <summary>
  /// Stops live backup by detaching merge-triggered backup handling and
  /// requesting scheduler cancellation.
  /// </summary>
  public void Stop()
  {
    lock (SyncRoot)
    {
      if (Status.Started)
      {
        Maintenance.OnMergeOperationEnded -= OnMergeOperationEnded;
        Status = Status with { Started = false };
      }

      var schedulerCancellation = SchedulerCancellation;
      schedulerCancellation?.Cancel();
      SchedulerCancellation = null;

      SignalStateChanged();
    }
  }

  /// <summary>
  /// Creates a live backup generation immediately.
  /// </summary>
  /// <remarks>
  /// Manual generations are not queued and do not require Start().
  /// If another generation is already running, this method throws.
  /// Async work runs without the caller's synchronization context; this method
  /// blocks until it completes. Use CreateGenerationAsync to avoid blocking.
  /// </remarks>
  public void CreateGeneration()
  {
    // Invoke pluggable async stores without the blocked caller's context.
    Task.Run(() => CreateGenerationAsync()).GetAwaiter().GetResult();
  }

  /// <summary>
  /// Creates a live backup generation immediately.
  /// </summary>
  /// <remarks>
  /// Manual generations are not queued and do not require Start().
  /// If another generation is already running, this method throws.
  /// </remarks>
  public async Task CreateGenerationAsync(
      CancellationToken cancellationToken = default)
  {
    ThrowIfDisposed();
    cancellationToken.ThrowIfCancellationRequested();

    BeginBackupGeneration(
        skipIfBusy: false,
        requireLiveBackupStarted: false,
        cancellationToken);

    await RunAcceptedGenerationAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Waits until live backup is stopped and all live backup activity has
  /// finished.
  /// </summary>
  /// <remarks>
  /// This method waits while Start() is active. Call Stop() to detach automatic
  /// triggers and request scheduler shutdown. The method also waits for the
  /// scheduler to exit and for any running generation to complete.
  /// Use WaitForLiveBackupAsync to wait without blocking the calling thread.
  /// </remarks>
  public void WaitForLiveBackup()
  {
    Task.Run(() => WaitForLiveBackupAsync()).GetAwaiter().GetResult();
  }

  /// <summary>
  /// Waits until live backup is stopped and all live backup activity has
  /// finished.
  /// </summary>
  /// <remarks>
  /// This method waits while Start() is active. Call Stop() to detach automatic
  /// triggers and request scheduler shutdown. The method also waits for the
  /// scheduler to exit and for any running generation to complete.
  /// </remarks>
  public async Task WaitForLiveBackupAsync(
      CancellationToken cancellationToken = default)
  {
    while (true)
    {
      Task waitTask;

      lock (SyncRoot)
      {
        if (!Status.Started &&
            Status.AttemptState != LiveBackupAttemptState.Running &&
            RunningSchedulerCount == 0)
        {
          return;
        }

        waitTask = StateChanged.Task;
      }

      await waitTask.WaitAsync(cancellationToken)
          .ConfigureAwait(false);
    }
  }

  void StartScheduler()
  {
    if (Options.Schedule.Kind == LiveBackupScheduleKind.None)
      return;

    SchedulerCancellation?.Cancel();

    var schedulerCancellation = new CancellationTokenSource();
    SchedulerCancellation = schedulerCancellation;
    ++RunningSchedulerCount;

    _ = RunSchedulerAsync(schedulerCancellation);
  }

  async Task RunSchedulerAsync(
      CancellationTokenSource schedulerCancellation)
  {
    var cancellationToken = schedulerCancellation.Token;

    try
    {
      var nextUtc = Options.Schedule.GetNextUtc(DateTime.UtcNow);

      while (true)
      {
        await DelayUntilUtcAsync(nextUtc, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        StartBackgroundBackupGeneration();

        nextUtc = Options.Schedule.GetNextUtc(DateTime.UtcNow);
      }
    }
    catch (OperationCanceledException)
        when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (Exception e)
    {
      LogBackupError(e);
    }
    finally
    {
      lock (SyncRoot)
      {
        --RunningSchedulerCount;

        if (ReferenceEquals(SchedulerCancellation, schedulerCancellation))
          SchedulerCancellation = null;

        schedulerCancellation.Dispose();

        SignalStateChanged();
      }
    }
  }

  static async Task DelayUntilUtcAsync(
      DateTime utcTime,
      CancellationToken cancellationToken)
  {
    while (true)
    {
      var delay = utcTime - DateTime.UtcNow;

      if (delay <= TimeSpan.Zero)
        return;

      if (delay > TimeSpan.FromHours(1))
        delay = TimeSpan.FromHours(1);

      await Task.Delay(delay, cancellationToken)
          .ConfigureAwait(false);
    }
  }

  void OnMergeOperationEnded(
      IZoneTreeMaintenance<TKey, TValue> zoneTree,
      MergeResult mergeResult)
  {
    StartBackupGenerationOnSuccessfulMerge(mergeResult);
  }

  void StartBackupGenerationOnSuccessfulMerge(MergeResult mergeResult)
  {
    try
    {
      if (Options.BackupAfterMerge &&
          mergeResult == MergeResult.SUCCESS)
      {
        StartBackgroundBackupGeneration();
      }
    }
    catch (Exception e)
    {
      LogBackupError(e);
    }
  }

  void StartBackgroundBackupGeneration()
  {
    if (!BeginBackupGeneration(
        skipIfBusy: true,
        requireLiveBackupStarted: true))
    {
      return;
    }

    _ = Task.Run(RunBackgroundBackupGenerationAsync);
  }

  Task RunBackgroundBackupGenerationAsync()
      => RunAcceptedGenerationAsync(CancellationToken.None, logFailure: true);

  bool BeginBackupGeneration(
      bool skipIfBusy,
      bool requireLiveBackupStarted,
      CancellationToken cancellationToken = default)
  {
    lock (SyncRoot)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (requireLiveBackupStarted &&
          !Status.Started)
      {
        return false;
      }

      if (IsDisposed)
      {
        if (skipIfBusy)
          return false;

        throw new ObjectDisposedException(GetType().Name);
      }

      if (Status.AttemptState == LiveBackupAttemptState.Running)
      {
        if (skipIfBusy)
          return false;

        throw new InvalidOperationException(
            "A live backup generation is already running.");
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

  async Task RunAcceptedGenerationAsync(CancellationToken cancellationToken, bool logFailure = false)
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
      if (logFailure) LogBackupError(exception);
      else throw;
    }
    finally
    {
      CompleteBackupGenerationActivity(outcome, generationId, error);
    }
  }

  void CompleteBackupGenerationActivity(LiveBackupAttemptState outcome, long generationId, string error)
  {
    lock (SyncRoot)
    {
      // BackupGenerationAsync has disposed the captured segment leases before
      // returning. Publish the outcome and release the busy state together.
      var finished = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      Status = Status with
      {
        AttemptState = outcome,
        LastFinishedAtUnixSeconds = finished,
        LastError = error,
        LastSuccessfulGenerationId = outcome == LiveBackupAttemptState.Succeeded ? generationId : Status.LastSuccessfulGenerationId,
        LastCompletedAtUnixSeconds = outcome == LiveBackupAttemptState.Succeeded ? finished : Status.LastCompletedAtUnixSeconds
      };
      SignalStateChanged();
    }
  }

  void SignalStateChanged()
  {
    var completion = StateChanged;
    StateChanged = CreateTaskSource();
    completion.TrySetResult(true);
  }

  async Task<long> BackupGenerationAsync(CancellationToken cancellationToken)
  {
    long generationId = 0;
    try
    {
      cancellationToken.ThrowIfCancellationRequested();

      using var segments = Capture();

      var startedAtUtc = DateTime.UtcNow;

      generationId = await Store.GetNextGenerationIdAsync(
          cancellationToken).ConfigureAwait(false);

      if (generationId <= 0)
        throw new InvalidOperationException(
            "Live backup store returned an invalid generation id.");

      lock (SyncRoot)
        Status = Status with { AttemptGenerationId = generationId };

      await Store.BeginGenerationAsync(
          generationId,
          startedAtUtc,
          segments.TreeKind,
          cancellationToken).ConfigureAwait(false);

      if (segments.InMemoryIterator != null)
      {
        await BackupInMemoryRecordsAsync(
            generationId,
            segments.InMemoryIterator,
            segments.AdditionalRecords,
            cancellationToken).ConfigureAwait(false);
      }

      await BackupDiskSegmentAsync(
          segments.DiskSegment,
          generationId,
          order: 0,
          cancellationToken).ConfigureAwait(false);

      if (segments.BottomSegments != null)
      {
        var order = 1;

        foreach (var bottomSegment in segments.BottomSegments)
        {
          await BackupDiskSegmentAsync(
              bottomSegment,
              generationId,
              order++,
              cancellationToken).ConfigureAwait(false);
        }
      }

      await Store.CompleteGenerationAsync(
          generationId,
          CoreZoneTree.LastOpIndex,
          cancellationToken).ConfigureAwait(false);
      return generationId;
    }
    catch (OperationCanceledException)
        when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (LiveBackupException)
    {
      throw;
    }
    catch (Exception e)
    {
      throw new LiveBackupGenerationException(
          generationId,
          "creating generation",
          e);
    }
  }

  async Task BackupDiskSegmentAsync(
      IDiskSegment<TKey, TValue> diskSegment,
      long generationId,
      int order,
      CancellationToken cancellationToken)
  {
    if (diskSegment == null ||
        diskSegment is NullDiskSegment<TKey, TValue> ||
        diskSegment.SegmentId == 0)
    {
      return;
    }

    var files = GetExistingSegmentFiles(diskSegment, order);

    if (files.Length == 0)
      return;

    await Parallel.ForEachAsync(
      files,
      new ParallelOptions
      {
        CancellationToken = cancellationToken,
        MaxDegreeOfParallelism = Options.MaxConcurrentFileTransfers
      },
      async (file, ct) =>
      {
        if (await Store.UseSegmentAsync(
            generationId,
            file,
            ct))
        {
          await CopySegmentFileAsync(
              file,
              generationId,
              ct);
        }
      });
  }

  DiskSegmentFile[] GetExistingSegmentFiles(
      IDiskSegment<TKey, TValue> diskSegment,
      int order)
  {
    var diskSegmentFiles = diskSegment.GetFiles();
    var files = new DiskSegmentFile[diskSegmentFiles.Length];
    var index = 0;

    foreach (var file in diskSegmentFiles)
    {
      if (string.IsNullOrWhiteSpace(file.Path))
      {
        throw new FileNotFoundException(
            $"Disk segment {diskSegment.SegmentId} returned an empty file path.");
      }

      if (!SourceDataFileStreamProvider.FileExists(file.Path))
      {
        throw new FileNotFoundException(
            $"Disk segment file was not found for segment {diskSegment.SegmentId}.",
            file.Path);
      }

      files[index++] = file with { Order = order };
    }

    return files;
  }

  async Task CopySegmentFileAsync(
      DiskSegmentFile file,
      long generationId,
      CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(file.Path))
      throw new FileNotFoundException(
          $"Disk segment {file.SegmentId} returned an empty file path.");

    if (!SourceDataFileStreamProvider.FileExists(file.Path))
    {
      throw new FileNotFoundException(
          $"Disk segment file was not found for segment {file.SegmentId}.",
          file.Path);
    }

    using var source = SourceDataFileStreamProvider.CreateFileStream(
        file.Path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite);

    await Store.UploadSegmentFileAsync(
        generationId,
        file,
        source.ToStream(),
        cancellationToken).ConfigureAwait(false);
  }

  async Task BackupInMemoryRecordsAsync(
      long generationId,
      IZoneTreeIterator<TKey, TValue> iterator,
      IReadOnlyList<KeyValuePair<TKey, TValue>> additionalRecords,
      CancellationToken cancellationToken)
  {
    // Generation IDs are allocated by the store and survive coordinator restarts.
    // A per-coordinator counter can overwrite an older generation's batch file.
    var batchId = generationId;
    var batch = new LiveBackupRecordBatch
    {
      BatchId = batchId,
      CompressionMethod = Options.RecordBatchCompression.Method,
      CompressionLevel = Options.RecordBatchCompression.Level,
      CompressionBlockSize = Options.RecordBatchCompression.BlockSize
    };

    await using var writer = await Store.OpenRecordWriterAsync(
        generationId,
        batch,
        cancellationToken).ConfigureAwait(false);

    long recordIndex = 0;

    while (iterator.Next())
    {
      cancellationToken.ThrowIfCancellationRequested();
      var key = iterator.CurrentKey;
      var value = iterator.CurrentValue;
      var keyBytes = ZoneTree.KeySerializer.Serialize(in key);
      var valueBytes = ZoneTree.ValueSerializer.Serialize(in value);

      ++recordIndex;

      var record = new LiveBackupRecord
      {
        RecordIndex = recordIndex,
        IsDeleted = ZoneTreeOptions.IsDeleted(in key, in value),
        Key = keyBytes,
        Value = valueBytes
      };

      await writer.WriteAsync(
          record,
          cancellationToken).ConfigureAwait(false);
    }

    // Pending statuses override commit markers that were not yet published
    // when a transactional snapshot was captured.
    foreach (var pair in additionalRecords)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var key = pair.Key;
      var value = pair.Value;
      await writer.WriteAsync(new LiveBackupRecord
      {
        RecordIndex = ++recordIndex,
        Key = ZoneTree.KeySerializer.Serialize(in key),
        Value = ZoneTree.ValueSerializer.Serialize(in value),
        IsDeleted = ZoneTreeOptions.IsDeleted(in key, in value)
      }, cancellationToken).ConfigureAwait(false);
    }

    batch.RecordCount = recordIndex;
  }

  static TaskCompletionSource<bool> CreateTaskSource()
  {
    return new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
  }

  void LogBackupError(Exception exception)
  {
    ZoneTree.Logger.LogError(exception);
  }

  void ThrowIfDisposed()
  {
    lock (SyncRoot)
    {
      ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
  }

  public void Dispose()
  {
    lock (SyncRoot)
    {
      if (IsDisposed)
        return;

      IsDisposed = true;
    }

    Maintenance.OnZoneTreeIsDisposing -= OnZoneTreeIsDisposing;
    Stop();
    WaitForLiveBackup();
    SchedulerCancellation?.Dispose();
    SchedulerCancellation = null;
  }
}
