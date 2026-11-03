using ZoneTree.AbstractFileStream;
using ZoneTree.Backup;
using ZoneTree.Core;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments.Disk;

namespace ZoneTree.UnitTests;

public sealed class LiveBackupStatusTests
{
  static LiveBackupStatus Empty => new(false, LiveBackupAttemptState.None, 0, 0, 0, 0, 0, string.Empty);

  [Test]
  public void InitialStatusAndAutomaticModeAreIndependentOfAttempts()
  {
    using var f = new Fixture();
    var store = new ControlledStore();
    using var backup = f.Create(store);
    Assert.That(backup.GetStatus(), Is.EqualTo(Empty));
    backup.Start();
    Assert.That(backup.GetStatus(), Is.EqualTo(Empty with { Started = true }));
    backup.Start();
    backup.Stop();
    Assert.That(backup.GetStatus(), Is.EqualTo(Empty));
    backup.Dispose();
    Assert.That(backup.GetStatus(), Is.EqualTo(Empty));
    Assert.That(store.Calls, Is.Zero);
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task RunningStartsBeforeAllocationAndSnapshotsNeverCallStorage(bool automaticMode)
  {
    using var f = new Fixture();
    var entered = Signal(); var release = Signal();
    var store = new ControlledStore
    {
      Allocate = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); return 42; }
    };
    using var backup = f.Create(store);
    if (automaticMode) backup.Start();
    var pending = backup.CreateGenerationAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try
    {
      var running = backup.GetStatus();
      AssertRunning(running, automaticMode, 0);
      var calls = store.Calls;
      for (var i = 0; i < 1000; ++i) Assert.That(backup.GetStatus(), Is.EqualTo(running));
      Assert.That(store.Calls, Is.EqualTo(calls));
      Assert.ThrowsAsync<InvalidOperationException>(() => backup.CreateGenerationAsync());
      Assert.That(backup.GetStatus(), Is.EqualTo(running), "Rejected busy requests must leave the accepted attempt untouched.");
      using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
      Assert.CatchAsync<OperationCanceledException>(() => backup.CreateGenerationAsync(cancelled.Token));
      Assert.That(backup.GetStatus(), Is.EqualTo(running));
    }
    finally { release.TrySetResult(); }
    await pending;
    AssertSucceeded(backup.GetStatus(), 42, automaticMode);
    Assert.That(backup.CurrentGenerationId, Is.EqualTo(42));
    backup.Stop();
  }

  [Test]
  public void CaptureFailureIsVisibleBeforeAnyStorageCall()
  {
    using var f = new Fixture();
    var store = new ControlledStore();
    LiveBackup<int, int> backup = null;
    using var owned = new LiveBackup<int, int>(f.Plain, Options(store), () =>
    {
      AssertRunning(backup.GetStatus(), false, 0);
      throw new IOException("capture failed");
    });
    backup = owned;
    Assert.ThrowsAsync<LiveBackupGenerationException>(() => backup.CreateGenerationAsync());
    AssertFailed(backup.GetStatus(), 0, "capture failed");
    Assert.That(store.Calls, Is.Zero);
  }

  [TestCase("allocation")]
  [TestCase("begin")]
  [TestCase("completion")]
  [TestCase("invalid-id")]
  public async Task FailurePreservesPreviousSuccessAndIncludesTheCause(string stage)
  {
    using var f = new Fixture();
    var store = new ControlledStore();
    using var backup = f.Create(store);
    await backup.CreateGenerationAsync();
    var previous = backup.GetStatus();
    var entered = Signal(); var release = Signal();
    store.Allocate = async token =>
    {
      entered.TrySetResult(); await release.Task.WaitAsync(token);
      if (stage == "allocation") throw new IOException("allocation failed");
      return stage == "invalid-id" ? -1 : 2;
    };
    store.Begin = (_, _) => stage == "begin" ? throw new IOException("begin failed") : Task.CompletedTask;
    store.Complete = (_, _) => stage == "completion" ? throw new IOException("completion failed") : Task.CompletedTask;
    var attempt = backup.CreateGenerationAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try
    {
      var running = backup.GetStatus();
      AssertRunning(running, false, 0);
      Assert.That(running.LastSuccessfulGenerationId, Is.EqualTo(previous.LastSuccessfulGenerationId));
      Assert.That(running.LastCompletedAtUnixSeconds, Is.EqualTo(previous.LastCompletedAtUnixSeconds));
    }
    finally { release.TrySetResult(); }
    Assert.ThrowsAsync<LiveBackupGenerationException>(async () => await attempt);
    var failed = backup.GetStatus();
    AssertFailed(failed, stage is "allocation" or "invalid-id" ? 0 : 2, stage == "invalid-id" ? "invalid generation id" : stage + " failed");
    Assert.That(failed.LastSuccessfulGenerationId, Is.EqualTo(previous.LastSuccessfulGenerationId));
    Assert.That(failed.LastCompletedAtUnixSeconds, Is.EqualTo(previous.LastCompletedAtUnixSeconds));
    Assert.That(backup.CurrentGenerationId, Is.EqualTo(previous.LastSuccessfulGenerationId));

    // Recover on the same coordinator: a new accepted attempt clears failure state.
    store.Allocate = _ => Task.FromResult(3L);
    store.Begin = store.Complete = null;
    backup.CreateGeneration();
    AssertSucceeded(backup.GetStatus(), 3, false);
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task AcceptedCancellationPreservesPreviousSuccessAndClearsErrors(bool beforeAllocation)
  {
    using var f = new Fixture();
    var store = new ControlledStore();
    using var backup = f.Create(store);
    await backup.CreateGenerationAsync();
    var previous = backup.GetStatus();
    store.Complete = (_, _) => throw new IOException("previous failure");
    Assert.ThrowsAsync<LiveBackupGenerationException>(() => backup.CreateGenerationAsync());
    Assert.That(backup.GetStatus().LastError, Does.Contain("previous failure"));
    var entered = Signal();
    using var cancellation = new CancellationTokenSource();
    async Task Pause(CancellationToken token)
    { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
    if (beforeAllocation) store.Allocate = async token => { await Pause(token); return 3; };
    store.Complete = (_, token) => Pause(token);
    var attempt = backup.CreateGenerationAsync(cancellation.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    AssertRunning(backup.GetStatus(), false, beforeAllocation ? 0 : 3);
    cancellation.Cancel();
    Assert.CatchAsync<OperationCanceledException>(async () => await attempt);
    var cancelled = backup.GetStatus();
    Assert.That(cancelled.AttemptState, Is.EqualTo(LiveBackupAttemptState.Cancelled));
    Assert.That(cancelled.LastError, Is.Empty);
    Assert.That(cancelled.LastFinishedAtUnixSeconds, Is.GreaterThan(0));
    Assert.That(cancelled.LastSuccessfulGenerationId, Is.EqualTo(previous.LastSuccessfulGenerationId));
    Assert.That(cancelled.LastCompletedAtUnixSeconds, Is.EqualTo(previous.LastCompletedAtUnixSeconds));
    store.Allocate = _ => Task.FromResult(4L); store.Complete = null;
    await backup.CreateGenerationAsync();
    AssertSucceeded(backup.GetStatus(), 4, false);
  }

  [Test]
  public async Task BackendCancellationWithoutRequestedTokenIsFailure()
  {
    using var f = new Fixture();
    var store = new ControlledStore { Complete = (_, _) => throw new OperationCanceledException("backend timed out") };
    using var backup = f.Create(store);
    Assert.ThrowsAsync<LiveBackupGenerationException>(() => backup.CreateGenerationAsync());
    AssertFailed(backup.GetStatus(), 1, "backend timed out");
    store.Complete = (_, _) => Task.CompletedTask;
    await backup.CreateGenerationAsync();
    AssertSucceeded(backup.GetStatus(), 2, false);
  }

  [Test]
  public async Task SuccessfulCompletionIsNotReclassifiedWhenCancellationWasNotObserved()
  {
    using var f = new Fixture();
    using var cancellation = new CancellationTokenSource();
    var store = new ControlledStore { Complete = (_, _) => { cancellation.Cancel(); return Task.CompletedTask; } };
    using var backup = f.Create(store);
    await backup.CreateGenerationAsync(cancellation.Token);
    AssertSucceeded(backup.GetStatus(), 1, false);
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task CancelledAndDisposedRejectionsLeaveStatusUntouched(bool priorSuccess)
  {
    using var f = new Fixture();
    var store = new ControlledStore();
    using var backup = f.Create(store);
    if (priorSuccess) await backup.CreateGenerationAsync();
    var previous = backup.GetStatus();
    var calls = store.Calls;
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Assert.CatchAsync<OperationCanceledException>(() => backup.CreateGenerationAsync(cancellation.Token));
    Assert.That(backup.GetStatus(), Is.EqualTo(previous));
    backup.Dispose();
    Assert.ThrowsAsync<ObjectDisposedException>(() => backup.CreateGenerationAsync());
    Assert.That(backup.GetStatus(), Is.EqualTo(previous));
    Assert.That(store.Calls, Is.EqualTo(calls));
  }

  [Test]
  public async Task DisposeWaitsForRunningAttemptAndKeepsFinalSnapshotReadable()
  {
    using var f = new Fixture();
    var entered = Signal(); var release = Signal();
    var store = new ControlledStore { Complete = async (_, _) => { entered.TrySetResult(); await release.Task; } };
    using var backup = f.Create(store);
    backup.Start();
    var attempt = backup.CreateGenerationAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var disposing = Task.Run(backup.Dispose);
    try
    {
      await WaitUntil(() => !backup.GetStatus().Started);
      AssertRunning(backup.GetStatus(), false, 1);
      var previous = backup.GetStatus();
      Assert.ThrowsAsync<ObjectDisposedException>(() => backup.CreateGenerationAsync());
      Assert.That(backup.GetStatus(), Is.EqualTo(previous));
      Assert.That(disposing.IsCompleted, Is.False);
    }
    finally { release.TrySetResult(); }
    await Task.WhenAll(attempt, disposing).WaitAsync(TimeSpan.FromSeconds(10));
    AssertSucceeded(backup.GetStatus(), 1, false);
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task TerminalStatusIsPublishedOnlyAfterCaptureCleanup(bool cleanupFails)
  {
    using var f = new Fixture();
    var entered = Signal();
    using var release = new ManualResetEventSlim();
    var store = new ControlledStore();
    using var backup = new LiveBackup<int, int>(f.Plain, Options(store), () => new ZoneTree<int, int>.BackupSegmentCollection
    {
      InMemoryIterator = new EmptyIterator(() =>
      {
        entered.TrySetResult();
        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cleanup was not released.");
        if (cleanupFails) throw new IOException("cleanup failed");
      })
    });
    var attempt = Task.Run(() => backup.CreateGenerationAsync());
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try
    {
      Assert.That(store.Published, Is.EqualTo(1));
      AssertRunning(backup.GetStatus(), false, 1);
      Assert.That(backup.CurrentGenerationId, Is.Zero);
      Assert.ThrowsAsync<InvalidOperationException>(() => backup.CreateGenerationAsync());
    }
    finally { release.Set(); }
    if (cleanupFails)
    {
      Assert.ThrowsAsync<LiveBackupGenerationException>(async () => await attempt);
      AssertFailed(backup.GetStatus(), 1, "cleanup failed");
      Assert.That(backup.CurrentGenerationId, Is.Zero);
    }
    else { await attempt; AssertSucceeded(backup.GetStatus(), 1, false); }
  }

  [TestCase(false, false)]
  [TestCase(false, true)]
  [TestCase(true, false)]
  [TestCase(true, true)]
  public async Task AutomaticAttemptsUseTheSameStatusAndStopDoesNotCancelThem(bool scheduled, bool fail)
  {
    var logger = new ErrorLogger();
    using var f = new Fixture(logger: logger);
    var entered = Signal(); var release = Signal();
    var store = new ControlledStore
    {
      Allocate = async _ => { entered.TrySetResult(); await release.Task; return 1; },
      Complete = (_, _) => fail ? throw new IOException("automatic failure") : Task.CompletedTask
    };
    var options = Options(store);
    options.BackupAfterMerge = !scheduled;
    if (scheduled) options.Schedule = LiveBackupSchedule.Every(TimeSpan.FromMilliseconds(20));
    using var backup = f.Plain.CreateLiveBackup(options);
    backup.Start();
    if (!scheduled)
    {
      f.Plain.Upsert(1, 100);
      f.Plain.Maintenance.MoveMutableSegmentForward();
      f.Plain.Maintenance.StartMergeOperation()?.Join();
    }
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try
    {
      AssertRunning(backup.GetStatus(), true, 0);
      backup.Stop();
      AssertRunning(backup.GetStatus(), false, 0);
    }
    finally { release.TrySetResult(); }
    await backup.WaitForLiveBackupAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if (fail)
    {
      AssertFailed(backup.GetStatus(), 1, "automatic failure");
      await logger.ErrorObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    else AssertSucceeded(backup.GetStatus(), 1, false);
  }

  [Test]
  public async Task MergeTriggerSkippedWhileBusyDoesNotReplaceManualAttemptStatus()
  {
    using var f = new Fixture();
    var entered = Signal(); var release = Signal();
    var store = new ControlledStore { Complete = async (_, _) => { entered.TrySetResult(); await release.Task; } };
    var options = Options(store); options.BackupAfterMerge = true;
    using var backup = f.Plain.CreateLiveBackup(options);
    backup.Start();
    var pending = backup.CreateGenerationAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try
    {
      var running = backup.GetStatus();
      var calls = store.Calls;
      f.Plain.Upsert(1, 100);
      f.Plain.Maintenance.MoveMutableSegmentForward();
      f.Plain.Maintenance.StartMergeOperation()?.Join();
      Assert.That(backup.GetStatus(), Is.EqualTo(running));
      Assert.That(store.Calls, Is.EqualTo(calls));
      backup.Stop();
    }
    finally { release.TrySetResult(); }
    await pending;
    AssertSucceeded(backup.GetStatus(), 1, false);
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task LocalProviderStatusWorksForPlainAndTransactionalTrees(bool transactional)
  {
    using var f = new Fixture(transactional);
    using var backup = f.Create(new LocalLiveBackupProvider(f.BackupPath));
    Assert.That(backup.GetStatus(), Is.EqualTo(Empty));
    await backup.CreateGenerationAsync();
    AssertSucceeded(backup.GetStatus(), 1, false);
    // Coordinator status is instance-local, even when the destination already contains backups.
    using var reopenedCoordinator = f.Create(new LocalLiveBackupProvider(f.BackupPath));
    Assert.That(reopenedCoordinator.GetStatus(), Is.EqualTo(Empty));
    await reopenedCoordinator.CreateGenerationAsync();
    AssertSucceeded(reopenedCoordinator.GetStatus(), 2, false);
  }

  [Test]
  public async Task PublishedObjectGenerationCanHaveFailedCoordinatorStatus()
  {
    using var f = new Fixture(transactional: true);
    var storage = new ObjectStorageLiveBackupTests.MemoryStorage();
    var provider = new ObjectStorageLiveBackupProvider(storage, new ObjectStorageLiveBackupOptions { KeepLastGenerations = 1 });
    using var backup = f.Create(provider);
    await backup.CreateGenerationAsync();
    var previous = backup.GetStatus();
    storage.BeforeDelete = _ => throw new IOException("retention denied");
    Assert.ThrowsAsync<LiveBackupGenerationException>(() => backup.CreateGenerationAsync());
    AssertFailed(backup.GetStatus(), 2, "retention denied");
    Assert.That(backup.GetStatus().LastSuccessfulGenerationId, Is.EqualTo(1));
    Assert.That(backup.GetStatus().LastCompletedAtUnixSeconds, Is.EqualTo(previous.LastCompletedAtUnixSeconds));
    await using var published = await provider.ReadLatestGenerationAsync(default);
    Assert.That(published.GenerationId, Is.EqualTo(2));
    Assert.That(backup.CurrentGenerationId, Is.EqualTo(1));
  }

  [Test]
  public async Task ConcurrentPollingAlwaysObservesAConsistentSnapshot()
  {
    using var f = new Fixture();
    var store = new ControlledStore { Complete = async (id, _) => { await Task.Yield(); if (id % 4 == 0) throw new IOException("expected failure"); } };
    using var backup = f.Create(store);
    var writing = Task.Run(async () =>
    {
      for (var i = 0; i < 100; ++i)
      { try { await backup.CreateGenerationAsync(); } catch (LiveBackupGenerationException) { } }
    });
    var reading = Task.Run(async () =>
    {
      do
      {
        var status = backup.GetStatus();
        switch (status.AttemptState)
        {
          case LiveBackupAttemptState.None: Assert.That(status, Is.EqualTo(Empty)); break;
          case LiveBackupAttemptState.Running: AssertRunning(status, false, status.AttemptGenerationId); break;
          case LiveBackupAttemptState.Succeeded: AssertSucceeded(status, status.AttemptGenerationId, false); break;
          case LiveBackupAttemptState.Failed: AssertFailed(status, status.AttemptGenerationId, "expected failure"); break;
          default: Assert.Fail("Unexpected state."); break;
        }
        await Task.Yield();
      } while (!writing.IsCompleted);
    });
    await Task.WhenAll(writing, reading).WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(backup.GetStatus().AttemptGenerationId, Is.EqualTo(100));
    Assert.That(backup.GetStatus().LastSuccessfulGenerationId, Is.EqualTo(99));
  }

  static LiveBackupOptions Options(ILiveBackupStore store) => new() { Store = store, BackupAfterMerge = false, IncludeInMemoryRecords = false };
  static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
  static async Task WaitUntil(Func<bool> predicate)
  {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!predicate()) await Task.Delay(1, timeout.Token);
  }
  static void AssertRunning(LiveBackupStatus status, bool started, long id)
  {
    Assert.That(status.Started, Is.EqualTo(started));
    Assert.That(status.AttemptState, Is.EqualTo(LiveBackupAttemptState.Running));
    Assert.That(status.AttemptGenerationId, Is.EqualTo(id));
    Assert.That(status.LastStartedAtUnixSeconds, Is.GreaterThan(0));
    Assert.That(status.LastFinishedAtUnixSeconds, Is.Zero);
    Assert.That(status.LastError, Is.Empty);
  }
  static void AssertSucceeded(LiveBackupStatus status, long id, bool started)
  {
    Assert.That(status.Started, Is.EqualTo(started));
    Assert.That(status.AttemptState, Is.EqualTo(LiveBackupAttemptState.Succeeded));
    Assert.That(status.AttemptGenerationId, Is.EqualTo(id));
    Assert.That(status.LastSuccessfulGenerationId, Is.EqualTo(id));
    Assert.That(id, Is.GreaterThan(0));
    Assert.That(status.LastStartedAtUnixSeconds, Is.GreaterThan(0));
    Assert.That(status.LastFinishedAtUnixSeconds, Is.GreaterThan(0));
    Assert.That(status.LastCompletedAtUnixSeconds, Is.EqualTo(status.LastFinishedAtUnixSeconds));
    Assert.That(status.LastError, Is.Empty);
  }
  static void AssertFailed(LiveBackupStatus status, long id, string message)
  {
    Assert.That(status.AttemptState, Is.EqualTo(LiveBackupAttemptState.Failed));
    Assert.That(status.AttemptGenerationId, Is.EqualTo(id));
    Assert.That(status.LastStartedAtUnixSeconds, Is.GreaterThan(0));
    Assert.That(status.LastFinishedAtUnixSeconds, Is.GreaterThan(0));
    Assert.That(status.LastError, Does.Contain(message));
  }

  sealed class ControlledStore : ILiveBackupStore
  {
    long Id;
    public int Calls, Published;
    public Func<CancellationToken, Task<long>> Allocate;
    public Func<long, CancellationToken, Task> Begin, Complete;
    public Task<long> GetNextGenerationIdAsync(CancellationToken token)
    { Interlocked.Increment(ref Calls); return Allocate?.Invoke(token) ?? Task.FromResult(Interlocked.Increment(ref Id)); }
    public Task BeginGenerationAsync(long id, DateTime utc, LiveBackupTreeKind kind, CancellationToken token)
    { Interlocked.Increment(ref Calls); return Begin?.Invoke(id, token) ?? Task.CompletedTask; }
    public Task<bool> UseSegmentAsync(long id, DiskSegmentFile file, CancellationToken token)
    { Interlocked.Increment(ref Calls); return Task.FromResult(false); }
    public Task UploadSegmentFileAsync(long id, DiskSegmentFile file, Stream source, CancellationToken token)
    { Interlocked.Increment(ref Calls); return Task.CompletedTask; }
    public Task<ILiveBackupRecordWriter> OpenRecordWriterAsync(long id, LiveBackupRecordBatch batch, CancellationToken token)
    { Interlocked.Increment(ref Calls); return Task.FromResult<ILiveBackupRecordWriter>(new EmptyWriter()); }
    public async Task CompleteGenerationAsync(long id, long index, CancellationToken token)
    { Interlocked.Increment(ref Calls); if (Complete != null) await Complete(id, token); Interlocked.Increment(ref Published); }
    sealed class EmptyWriter : ILiveBackupRecordWriter
    {
      public Task WriteAsync(LiveBackupRecord record, CancellationToken token) => Task.CompletedTask;
      public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
  }
  sealed class EmptyIterator(Action dispose) : IZoneTreeIterator<int, int>
  {
    public int CurrentKey => throw new InvalidOperationException();
    public int CurrentValue => throw new InvalidOperationException();
    public KeyValuePair<int, int> Current => throw new InvalidOperationException();
    public bool HasCurrent => false;
    public bool AutoRefresh => false;
    public bool Next() => false;
    public void Seek(in int key) { }
    public void SeekFirst() { }
    public void Refresh() { }
    public void Dispose() => dispose();
  }
  sealed class ErrorLogger : ILogger
  {
    public TaskCompletionSource ErrorObserved { get; } = Signal();
    public LogLevel LogLevel { get; set; }
    public void LogError(Exception error) => ErrorObserved.TrySetResult();
    public void LogInfo(object message) { }
    public void LogWarning(object message) { }
    public void LogTrace(object message) { }
  }
  sealed class Fixture : IDisposable
  {
    readonly string Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, nameof(LiveBackupStatusTests), Guid.NewGuid().ToString("N"));
    public string BackupPath => Path.Combine(Root, "backup");
    public IZoneTree<int, int> Plain { get; }
    readonly ITransactionalZoneTree<int, int> Transactional;
    public Fixture(bool transactional = false, ILogger logger = null)
    {
      var factory = new ZoneTreeFactory<int, int>(new InMemoryFileStreamProvider()).SetDataDirectory("source")
          .SetLogger(logger ?? SilentTestLogger.Instance)
          .ConfigureWriteAheadLogOptions(o => o.WriteAheadLogMode = WriteAheadLogMode.Shared);
      if (transactional) Transactional = factory.CreateTransactional(); else Plain = factory.Create();
    }
    public ILiveBackup Create(ILiveBackupStore store)
    {
      var options = Options(store);
      if (Transactional == null) return Plain.CreateLiveBackup(options);
      options.IncludeInMemoryRecords = true;
      return Transactional.CreateLiveBackup(options);
    }
    public void Dispose() { Plain?.Dispose(); Transactional?.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
  }
}
