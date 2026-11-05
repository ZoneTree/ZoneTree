using System.Collections.Concurrent;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Transactional;

namespace ZoneTree.UnitTests;

/// <summary>
/// Calls the operations of one MVCC transaction from several threads at once.
/// </summary>
public sealed class MvccConcurrentTransactionTests
{
  string DataDirectory;

  [SetUp]
  public void SetUp()
  {
    DataDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
        nameof(MvccConcurrentTransactionTests), Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(DataDirectory))
      Directory.Delete(DataDirectory, true);
  }

  ITransactionalZoneTree<int, int> OpenTree() => new ZoneTreeFactory<int, int>()
      .SetDataDirectory(DataDirectory)
      .SetLogger(new ConsoleLogger(LogLevel.Error))
      .SetMutableSegmentMaxItemCount(2_000)
      .OpenOrCreateTransactional();

  [Test]
  public void ConcurrentWritesOfOneTransactionAllCommit()
  {
    using var tree = OpenTree();
    using (var transaction = tree.BeginTransaction())
    {
      Parallel.For(0, 20_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, key =>
      {
        transaction.Upsert(key, key);
        if (key % 3 == 0)
          transaction.Delete(key);
        Assert.That(transaction.TryGet(key, out _), Is.EqualTo(key % 3 != 0));
      });
      transaction.Commit();
    }
    using var read = tree.BeginReadOnlyTransaction();
    Assert.That(read.Range(0, 20_000).Count(), Is.EqualTo(20_000 - (20_000 + 2) / 3));
  }

  [Test]
  public void ConcurrentReadsOfOneTransactionKeepTheWholeReadSet()
  {
    using var tree = OpenTree();
    using (var setup = tree.BeginTransaction())
    {
      for (var key = 0; key < 2_000; ++key)
        setup.Upsert(key, key);
      setup.Commit();
    }
    for (var changed = 0; changed < 2_000; changed += 397)
    {
      using var reader = tree.BeginTransaction(TransactionIsolationLevel.Serializable);
      Parallel.For(0, 2_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, key =>
      {
        if (key % 2 == 0)
          reader.TryGet(key, out _);
        else
          reader.TryGetForUpdate(key, out _);
      });
      using (var writer = tree.BeginTransaction())
      {
        writer.Upsert(changed, -1);
        writer.Commit();
      }
      reader.Upsert(-1, changed);
      // Every read joined the read set, so the change of any read key conflicts.
      Assert.That(reader.TryCommit(), Is.False, $"key {changed}");
    }
  }

  [Test]
  public void ConcurrentIteratorsOfOneTransactionEachJoinTheReadSet()
  {
    using var tree = OpenTree();
    using (var setup = tree.BeginTransaction())
    {
      for (var key = 0; key < 1_000; ++key)
        setup.Upsert(key, key);
      setup.Commit();
    }
    foreach (var changed in new[] { 10, 510 })
    {
      using var reader = tree.BeginTransaction(TransactionIsolationLevel.Serializable);
      var counts = new int[8];
      Parallel.For(0, counts.Length, index =>
      {
        var from = index % 2 * 500;
        counts[index] = reader.Range(from, from + 499).Count();
      });
      Assert.That(counts, Is.All.EqualTo(500));
      using (var writer = tree.BeginTransaction())
      {
        writer.Upsert(changed, -1);
        writer.Commit();
      }
      reader.Upsert(-1, changed);
      Assert.That(reader.TryCommit(), Is.False, $"key {changed}");
    }
  }

  [Test]
  public async Task CommitWaitsForRunningOperations()
  {
    using var tree = OpenTree();
    var transaction = tree.BeginTransaction();
    var written = new ConcurrentBag<int>();
    using var started = new CountdownEvent(4);
    // Writers keep writing until the commit stops them; every write that returned commits.
    var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
    {
      started.Signal();
      for (var key = writer * 1_000_000; ; ++key)
      {
        try { transaction.Upsert(key, key); }
        catch (TransactionCommitInProgressException) { return; }
        catch (TransactionAlreadyCommittedException) { return; }
        written.Add(key);
      }
    })).ToArray();
    started.Wait();
    while (written.Count < 1_000)
      Thread.Yield();
    transaction.Commit();
    await Task.WhenAll(writers);
    transaction.Dispose();
    using var read = tree.BeginReadOnlyTransaction();
    var committed = read.Range(int.MinValue, int.MaxValue).Select(x => x.Key).ToHashSet();
    Assert.That(committed.SetEquals(written), Is.True);
  }

  [Test]
  public async Task RollbackWaitsForRunningOperations()
  {
    using var tree = OpenTree();
    var transaction = tree.BeginTransaction();
    var writes = 0;
    var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
    {
      for (var key = writer * 1_000_000; ; ++key)
      {
        try { transaction.Upsert(key, key); }
        catch (TransactionAbortedException) { return; }
        Interlocked.Increment(ref writes);
      }
    })).ToArray();
    while (Volatile.Read(ref writes) < 1_000)
      Thread.Yield();
    // Concurrent rollbacks end the transaction once.
    Parallel.For(0, 4, _ => transaction.Rollback());
    await Task.WhenAll(writers);
    Assert.That(transaction.State, Is.EqualTo(TransactionState.Aborted));
    Assert.Throws<TransactionAbortedException>(() => transaction.Commit());
    Assert.That(tree.Maintenance.ActiveTransactionCount, Is.Zero);
    using var read = tree.BeginReadOnlyTransaction();
    Assert.That(read.Range(int.MinValue, int.MaxValue).Any(), Is.False);
  }

  [Test]
  public void ConcurrentCommitsOfOneTransactionCommitOnce()
  {
    using var tree = OpenTree();
    var transaction = tree.BeginTransaction();
    transaction.Upsert(1, 1);
    var commits = 0;
    var errors = new ConcurrentBag<Exception>();
    Parallel.For(0, 8, _ =>
    {
      try
      {
        transaction.Commit();
        Interlocked.Increment(ref commits);
      }
      catch (Exception error) { errors.Add(error); }
    });
    Assert.That(commits, Is.EqualTo(1));
    Assert.That(errors, Has.Count.EqualTo(7));
    Assert.That(errors, Has.All.InstanceOf<TransactionAlreadyCommittedException>()
        .Or.InstanceOf<TransactionCommitInProgressException>());
    Assert.That(transaction.State, Is.EqualTo(TransactionState.Committed));
    transaction.Dispose();
  }
}
