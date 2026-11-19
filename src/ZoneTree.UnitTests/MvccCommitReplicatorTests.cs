using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Transactional;
using ZoneTree.Transactional.Mvcc;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.UnitTests;

/// <summary>
/// A tree whose commits another log replicates publishes each commit only once its replication
/// completed, writes no commit point of its own, and stops its commits when a replication fails.
/// </summary>
public sealed class MvccCommitReplicatorTests
{
  sealed class Replicator(MvccZoneTree<int, int> tree) : IMvccCommitReplicator<int>
  {
    internal readonly List<(long TransactionId, int[] Keys, long CommitTimestamp)> Replicated = [];
    internal TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Fails;

    public object Capture(long transactionId, int[] keys) => (transactionId, keys);

    internal int Syncs;

    public Task SyncAsync()
    {
      Interlocked.Increment(ref Syncs);
      return Task.CompletedTask;
    }

    public async Task Replicate(object captured, long commitTimestamp)
    {
      var (transactionId, keys) = ((long, int[]))captured;
      lock (Replicated)
        Replicated.Add((transactionId, keys, commitTimestamp));
      await Gate.Task.ConfigureAwait(false);
      if (Fails)
        throw new InvalidOperationException("The replicas lost the commit.");
      // The replication writes the commit point, as the replicas' group does.
      tree.Storage.ForceDelete(VersionedKey<int>.TransactionStatus(transactionId));
    }
  }

  static ITransactionalZoneTree<int, int> Create(string directory)
      => new ZoneTreeFactory<int, int>()
          .SetDataDirectory(directory)
          .SetLogger(SilentTestLogger.Instance)
          .ConfigureWriteAheadLogOptions(options => options.Mode = WriteAheadLogMode.None)
          .OpenOrCreateTransactional();

  static int? Read(ITransactionalZoneTree<int, int> tree, int key)
  {
    using var reader = tree.BeginReadOnlyTransaction();
    return reader.TryGet(key, out var value) ? value : null;
  }

  [Test]
  public async Task ACommitIsPublishedOnceItsReplicationCompleted()
  {
    var directory = Path.Combine(Path.GetTempPath(), "zonetree-replicator-" + Guid.NewGuid().ToString("N"));
    try
    {
      using var tree = Create(directory);
      var mvcc = (MvccZoneTree<int, int>)tree;
      var replicator = new Replicator(mvcc);
      mvcc.CommitReplicator = replicator;

      using var transaction = tree.BeginTransaction();
      transaction.Upsert(1, 10);
      transaction.Upsert(2, 20);
      var commit = Task.Run(transaction.Commit);

      // The commit waits for its replication, and the tree wrote no commit point meanwhile.
      await Task.Delay(100);
      Assert.That(commit.IsCompleted, Is.False);
      Assert.That(Read(tree, 1), Is.Null);
      Assert.That(mvcc.Storage.TryGet(VersionedKey<int>.TransactionStatus(transaction.TransactionId), out _), Is.True);
      Assert.That(replicator.Replicated, Has.Count.EqualTo(1));
      Assert.That(replicator.Replicated[0].Keys, Is.EquivalentTo(new[] { 1, 2 }));

      replicator.Gate.SetResult();
      await commit;
      Assert.That(Read(tree, 1), Is.EqualTo(10));
      Assert.That(Read(tree, 2), Is.EqualTo(20));
      Assert.That(replicator.Replicated[0].CommitTimestamp, Is.EqualTo(transaction.CommitTimestamp));

      // A transaction that wrote nothing has nothing to replicate.
      using (var empty = tree.BeginTransaction())
        empty.Commit();
      Assert.That(replicator.Replicated, Has.Count.EqualTo(1));
    }
    finally
    {
      if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    }
  }

  [Test]
  public async Task ADurableCommitAndASyncWaitForTheReplicasToMakeItDurable()
  {
    var directory = Path.Combine(Path.GetTempPath(), "zonetree-replicator-" + Guid.NewGuid().ToString("N"));
    try
    {
      using var tree = Create(directory);
      var mvcc = (MvccZoneTree<int, int>)tree;
      var replicator = new Replicator(mvcc);
      mvcc.CommitReplicator = replicator;
      replicator.Gate.SetResult();

      using (var ordinary = tree.BeginTransaction())
      {
        ordinary.Upsert(1, 10);
        ordinary.Commit();
      }
      Assert.That(replicator.Syncs, Is.Zero);
      using (var durable = tree.BeginTransaction())
      {
        durable.Upsert(2, 20);
        await durable.CommitDurableAsync();
      }
      Assert.That(replicator.Syncs, Is.EqualTo(1));
      Assert.That(Read(tree, 2), Is.EqualTo(20));
      await tree.SyncWalAsync();
      Assert.That(replicator.Syncs, Is.EqualTo(2));
    }
    finally
    {
      if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    }
  }

  [Test]
  public void AFailedReplicationLeavesItsCommitInDoubtAndStopsCommits()
  {
    var directory = Path.Combine(Path.GetTempPath(), "zonetree-replicator-" + Guid.NewGuid().ToString("N"));
    try
    {
      using var tree = Create(directory);
      var mvcc = (MvccZoneTree<int, int>)tree;
      var replicator = new Replicator(mvcc) { Fails = true };
      mvcc.CommitReplicator = replicator;
      replicator.Gate.SetResult();

      using var transaction = tree.BeginTransaction();
      transaction.Upsert(1, 10);
      Assert.Throws<InvalidOperationException>(transaction.Commit);
      Assert.That(transaction.State, Is.EqualTo(TransactionState.InDoubt));
      Assert.That(Read(tree, 1), Is.Null);

      using var next = tree.BeginTransaction();
      next.Upsert(2, 20);
      Assert.Throws<TransactionalTreeCommitsStoppedException>(next.Commit);
    }
    finally
    {
      if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    }
  }
}
