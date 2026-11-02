using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Transactional;

namespace ZoneTree.UnitTests;

public sealed class MvccDurableVisibilityTests
{
  static ZoneTreeFactory<int, int> Factory(PowerLossFileStreamProvider files, WriteAheadLogMode mode)
      => new ZoneTreeFactory<int, int>(files)
          .SetDataDirectory("data")
          .SetLogger(SilentTestLogger.Instance)
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .SetMutableSegmentMaxItemCount(1000)
          .ConfigureWriteAheadLogOptions(options =>
          {
            options.WriteAheadLogMode = mode;
            options.CompressionBlockSize = 4096;
            options.SyncCompressedModeOptions.EnableTailWriterJob = false;
            options.AsyncCompressedModeOptions.EmptyQueuePollInterval = 10000;
          });

  static bool IsJournal(string path) => path.Contains("/seg/shared/", StringComparison.Ordinal) &&
      path.EndsWith(".wal", StringComparison.Ordinal);

  static int? Read(ITransactionalZoneTree<int, int> tree, int key)
  {
    using var reader = tree.BeginReadOnlyTransaction();
    return reader.TryGet(key, out var value) ? value : null;
  }

  [TestCase(WriteAheadLogMode.Shared, "commit")]
  [TestCase(WriteAheadLogMode.Shared, "try")]
  [TestCase(WriteAheadLogMode.Shared, "execute-result")]
  [TestCase(WriteAheadLogMode.Shared, "execute-action")]
  [TestCase(WriteAheadLogMode.AsyncShared, "commit")]
  [TestCase(WriteAheadLogMode.AsyncShared, "try")]
  [TestCase(WriteAheadLogMode.AsyncShared, "execute-result")]
  [TestCase(WriteAheadLogMode.AsyncShared, "execute-action")]
  public async Task DurableApisPublishOnlyAfterSync(WriteAheadLogMode mode, string api)
  {
    var files = new PowerLossFileStreamProvider();
    using var tree = Factory(files, mode).CreateTransactional();
    await tree.ExecuteDurableAsync(tx => tx.Upsert(1, 10));
    using var oldReader = tree.BeginReadOnlyTransaction();
    var oldTimestamp = tree.Maintenance.LastCommitTimestamp;
    using var entered = new ManualResetEventSlim();
    using var resume = new ManualResetEventSlim();
    files.BeforeDiskSync = path =>
    {
      if (!IsJournal(path)) return;
      entered.Set();
      if (!resume.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Sync was not resumed.");
    };
    var commit = Task.Run(async () =>
    {
      void Write(ITransaction<int, int> tx)
      {
        tx.Upsert(1, 20);
        tree.Maintenance.ZoneTree.Maintenance.MoveMutableSegmentForward();
        tx.Upsert(2, 30);
      }
      if (api == "execute-result")
        Assert.That(await tree.ExecuteDurableAsync(tx => { Write(tx); return 123; }), Is.EqualTo(123));
      else if (api == "execute-action")
        await tree.ExecuteDurableAsync(Write);
      else
      {
        using var tx = tree.BeginTransaction();
        Write(tx);
        if (api == "try") Assert.That(await tx.TryCommitDurableAsync(), Is.True);
        else await tx.CommitDurableAsync();
        Assert.That(tx.State, Is.EqualTo(TransactionState.Committed));
      }
    });
    try
    {
      Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
      Assert.That(commit.IsCompleted, Is.False);
      Assert.That(Read(tree, 1), Is.EqualTo(10));
      Assert.That(Read(tree, 2), Is.Null);
      Assert.That(tree.Maintenance.LastCommitTimestamp, Is.EqualTo(oldTimestamp));
      await Task.Run(() => tree.Maintenance.ZoneTree.Maintenance.MoveMutableSegmentForward())
          .WaitAsync(TimeSpan.FromSeconds(5));
    }
    finally
    {
      resume.Set();
      await commit.WaitAsync(TimeSpan.FromSeconds(10));
      files.BeforeDiskSync = null;
    }
    Assert.That(Read(tree, 1), Is.EqualTo(20));
    Assert.That(Read(tree, 2), Is.EqualTo(30));
    Assert.That(oldReader.TryGet(1, out var previous), Is.True);
    Assert.That(previous, Is.EqualTo(10));
    Assert.That(oldReader.ContainsKey(2), Is.False);
    using var recovered = Factory(files.SimulatePowerLoss(), mode).OpenTransactional();
    Assert.That(Read(recovered, 1), Is.EqualTo(20));
    Assert.That(Read(recovered, 2), Is.EqualTo(30));
  }

  [Test]
  public async Task PendingCommitsKeepTheirOrderAndShareSyncs()
  {
    var files = new PowerLossFileStreamProvider();
    using var tree = Factory(files, WriteAheadLogMode.Shared).CreateTransactional();
    await tree.ExecuteDurableAsync(tx => tx.Upsert(1, 10));
    using var first = tree.BeginTransaction();
    first.Upsert(1, 20);
    using var entered = new ManualResetEventSlim();
    using var resume = new ManualResetEventSlim();
    var syncs = 0;
    files.BeforeDiskSync = path =>
    {
      if (!IsJournal(path)) return;
      Interlocked.Increment(ref syncs);
      entered.Set();
      if (!resume.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Sync was not resumed.");
    };
    var firstCommit = first.CommitDurableAsync();
    Task ordinaryCommit = Task.CompletedTask;
    Task secondCommit = Task.CompletedTask;
    Task thirdCommit = Task.CompletedTask;
    using var ordinary = tree.BeginTransaction();
    using var second = tree.BeginTransaction();
    using var third = tree.BeginTransaction();
    try
    {
      Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
      // A reader of the old value must conflict with the invisible prepared commit.
      using (var conflicting = tree.BeginTransaction())
      {
        Assert.That(conflicting.TryGet(1, out var old), Is.True);
        Assert.That(old, Is.EqualTo(10));
        conflicting.Upsert(9, 90);
        Assert.That(await conflicting.TryCommitDurableAsync().WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
      }
      ordinary.Upsert(2, 30);
      ordinaryCommit = Task.Run(ordinary.Commit);
      Assert.That(SpinWait.SpinUntil(() => ordinary.State == TransactionState.Committing,
          TimeSpan.FromSeconds(5)), Is.True);
      second.Upsert(3, 40);
      secondCommit = second.CommitDurableAsync();
      third.Upsert(4, 50);
      thirdCommit = third.CommitDurableAsync();
      Assert.That(second.State, Is.EqualTo(TransactionState.Committing));
      Assert.That(third.State, Is.EqualTo(TransactionState.Committing));
      Assert.That(Read(tree, 1), Is.EqualTo(10));
      Assert.That(Read(tree, 2), Is.Null);
      Assert.That(Read(tree, 3), Is.Null);
      Assert.That(Read(tree, 4), Is.Null);
    }
    finally
    {
      resume.Set();
      await Task.WhenAll(firstCommit, ordinaryCommit, secondCommit, thirdCommit).WaitAsync(TimeSpan.FromSeconds(10));
      files.BeforeDiskSync = null;
    }
    Assert.That(ordinary.CommitTimestamp, Is.GreaterThan(first.CommitTimestamp));
    Assert.That(second.CommitTimestamp, Is.GreaterThan(ordinary.CommitTimestamp));
    Assert.That(third.CommitTimestamp, Is.GreaterThan(second.CommitTimestamp));
    Assert.That(syncs, Is.EqualTo(2), "later durable commits share the next fsync");
    using var recovered = Factory(files.SimulatePowerLoss(), WriteAheadLogMode.Shared).OpenTransactional();
    for (var key = 1; key <= 4; ++key) Assert.That(Read(recovered, key), Is.EqualTo(10 + 10 * key));
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task CancellationStopsWaitingWithoutPublishingOrRollingBackEarly(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    using var tree = Factory(files, mode).CreateTransactional();
    await tree.ExecuteDurableAsync(tx => tx.Upsert(1, 10));
    using var tx = tree.BeginTransaction();
    tx.Upsert(1, 20);
    using var entered = new ManualResetEventSlim();
    using var resume = new ManualResetEventSlim();
    using var cancellation = new CancellationTokenSource();
    files.BeforeDiskSync = path =>
    {
      if (!IsJournal(path)) return;
      entered.Set();
      if (!resume.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Sync was not resumed.");
    };
    var commit = tx.CommitDurableAsync(cancellation.Token);
    try
    {
      Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
      cancellation.Cancel();
      Assert.ThrowsAsync<TaskCanceledException>(async () => await commit.WaitAsync(TimeSpan.FromSeconds(5)));
      Assert.That(tx.State, Is.EqualTo(TransactionState.Committing));
      tx.Dispose();
      Assert.That(Read(tree, 1), Is.EqualTo(10));
    }
    finally
    {
      resume.Set();
      Assert.That(SpinWait.SpinUntil(() => tx.State == TransactionState.Committed, TimeSpan.FromSeconds(10)), Is.True);
      files.BeforeDiskSync = null;
    }
    Assert.That(Read(tree, 1), Is.EqualTo(20));
    using var recovered = Factory(files.SimulatePowerLoss(), mode).OpenTransactional();
    Assert.That(Read(recovered, 1), Is.EqualTo(20));
  }

  [TestCase(WriteAheadLogMode.Shared)]
  [TestCase(WriteAheadLogMode.AsyncShared)]
  public async Task FailedSyncDoesNotPublishOrRollbackTheCommit(WriteAheadLogMode mode)
  {
    var files = new PowerLossFileStreamProvider();
    var tree = Factory(files, mode).CreateTransactional();
    try
    {
      await tree.ExecuteDurableAsync(tx => tx.Upsert(1, 10));
      using var tx = tree.BeginTransaction();
      tx.Upsert(1, 20);
      files.FailDiskSync = IsJournal;
      Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(() => tx.CommitDurableAsync());
      Assert.That(tx.State, Is.EqualTo(TransactionState.InDoubt));
      Assert.That(Read(tree, 1), Is.EqualTo(10));
      tx.Rollback();
      Assert.That(tx.State, Is.EqualTo(TransactionState.InDoubt));
      Assert.That(tree.Maintenance.ActiveTransactionCount, Is.Zero);
      using var recovered = Factory(files.SimulatePowerLoss(), mode).OpenTransactional();
      Assert.That(Read(recovered, 1), Is.EqualTo(10));
    }
    finally
    {
      files.FailDiskSync = null;
      Assert.Throws<WriteAheadLogSyncFailedException>(tree.Dispose);
      Assert.That(files.OpenStreamCount, Is.Zero);
    }
  }

  [Test]
  public async Task FailedSyncDoesNotPublishLaterPreparedCommits()
  {
    var files = new PowerLossFileStreamProvider();
    var tree = Factory(files, WriteAheadLogMode.Shared).CreateTransactional();
    using var entered = new ManualResetEventSlim();
    using var resume = new ManualResetEventSlim();
    Task firstCommit = Task.CompletedTask;
    Task laterCommit = Task.CompletedTask;
    try
    {
      await tree.ExecuteDurableAsync(tx => tx.Upsert(1, 10));
      using var first = tree.BeginTransaction();
      first.Upsert(1, 20);
      files.BeforeDiskSync = path =>
      {
        if (!IsJournal(path)) return;
        entered.Set();
        if (!resume.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Sync was not resumed.");
      };
      files.FailDiskSync = IsJournal;
      firstCommit = first.CommitDurableAsync();
      Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
      using var later = tree.BeginTransaction();
      later.Upsert(2, 30);
      laterCommit = Task.Run(later.Commit);
      Assert.That(SpinWait.SpinUntil(() => later.State == TransactionState.Committing,
          TimeSpan.FromSeconds(5)), Is.True);
      resume.Set();
      Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(async () => await firstCommit);
      Assert.ThrowsAsync<WriteAheadLogSyncFailedException>(async () => await laterCommit);
      Assert.That(first.State, Is.EqualTo(TransactionState.InDoubt));
      Assert.That(later.State, Is.EqualTo(TransactionState.InDoubt));
      Assert.That(Read(tree, 1), Is.EqualTo(10));
      Assert.That(Read(tree, 2), Is.Null);
      Assert.That(tree.Maintenance.ActiveTransactionCount, Is.Zero);
    }
    finally
    {
      resume.Set();
      try { await Task.WhenAll(firstCommit, laterCommit).WaitAsync(TimeSpan.FromSeconds(10)); }
      catch (WriteAheadLogSyncFailedException) { }
      files.BeforeDiskSync = null;
      files.FailDiskSync = null;
      Assert.Throws<WriteAheadLogSyncFailedException>(tree.Dispose);
    }
  }

  [Test]
  public async Task CancellationBeforeCommitLeavesTheTransactionActive()
  {
    var files = new PowerLossFileStreamProvider();
    using var tree = Factory(files, WriteAheadLogMode.Shared).CreateTransactional();
    await tree.ExecuteDurableAsync(tx => tx.Upsert(1, 10));
    using var tx = tree.BeginTransaction();
    tx.Upsert(1, 20);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var syncs = files.DiskSyncCount;
    Assert.ThrowsAsync<OperationCanceledException>(() => tx.CommitDurableAsync(cancellation.Token));
    Assert.That(tx.State, Is.EqualTo(TransactionState.Active));
    Assert.That(files.DiskSyncCount, Is.EqualTo(syncs));
    Assert.That(Read(tree, 1), Is.EqualTo(10));
  }
}
