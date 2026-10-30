using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.Transactional;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.UnitTests;

public sealed class MvccZoneTreeTests
{
  string DataDirectory;

  [SetUp]
  public void SetUp()
  {
    DataDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
        nameof(MvccZoneTreeTests), Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(DataDirectory))
      Directory.Delete(DataDirectory, true);
  }

  ZoneTreeFactory<int, int> CreateFactory(
      int mutableSegmentMaxItemCount = 1000,
      DiskSegmentMode diskSegmentMode = DiskSegmentMode.SingleDiskSegment,
      WriteAheadLogMode walMode = WriteAheadLogMode.Sync)
  {
    return new ZoneTreeFactory<int, int>()
        .SetDataDirectory(DataDirectory)
        .SetLogger(new ConsoleLogger(LogLevel.Error))
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(mutableSegmentMaxItemCount)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DiskSegmentMode = diskSegmentMode;
          options.MinimumRecordCount = 2;
          options.MaximumRecordCount = 16;
        })
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = walMode);
  }

  static void Write(ITransactionalZoneTree<int, int> tree, params (int Key, int Value)[] records)
  {
    using var tx = tree.BeginTransaction();
    foreach (var (key, value) in records)
      tx.Upsert(key, value);
    tx.Commit();
  }

  static int? Read(ITransaction<int, int> tx, int key)
      => tx.TryGet(key, out var value) ? value : null;

  static int? ReadCommitted(ITransactionalZoneTree<int, int> tree, int key)
  {
    using var tx = tree.BeginReadOnlyTransaction();
    return Read(tx, key);
  }

  static List<KeyValuePair<int, int>> Scan(ITransaction<int, int> tx, bool reverse = false)
  {
    using var iterator = reverse ? tx.CreateReverseIterator() : tx.CreateIterator();
    var result = new List<KeyValuePair<int, int>>();
    while (iterator.Next())
      result.Add(iterator.Current);
    return result;
  }

  static int CountStoredVersions(ITransactionalZoneTree<int, int> tree, int key)
  {
    using var iterator = tree.Maintenance.ZoneTree.CreateIterator(IteratorType.NoRefresh, false, false);
    iterator.Seek(VersionedKey<int>.Data(key, long.MaxValue));
    var count = 0;
    while (iterator.Next() &&
        iterator.CurrentKey.Kind == VersionedKeyKind.Data &&
        iterator.CurrentKey.Key == key)
      ++count;
    return count;
  }

  static void MergeAll(ITransactionalZoneTree<int, int> tree)
  {
    var maintenance = tree.Maintenance.ZoneTree.Maintenance;
    maintenance.MoveMutableSegmentForward();
    maintenance.StartMergeOperation()?.Join();
  }

  [Test]
  public void WritesAreInvisibleUntilCommit()
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    using var writer = tree.BeginTransaction();
    writer.Upsert(1, 10);
    Assert.That(Read(writer, 1), Is.EqualTo(10), "a transaction reads its own writes");
    Assert.That(ReadCommitted(tree, 1), Is.Null);

    using var concurrent = tree.BeginTransaction();
    writer.Commit();
    Assert.That(writer.State, Is.EqualTo(TransactionState.Committed));
    Assert.That(writer.CommitTimestamp, Is.GreaterThan(writer.SnapshotTimestamp));
    Assert.That(Read(concurrent, 1), Is.Null, "an older snapshot does not see the commit");
    Assert.That(ReadCommitted(tree, 1), Is.EqualTo(10));
  }

  [Test]
  public void DeleteHidesTheKeyAndOlderSnapshotsKeepIt()
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    Write(tree, (1, 10), (2, 20));
    using var oldReader = tree.BeginReadOnlyTransaction();
    using (var tx = tree.BeginTransaction())
    {
      tx.Delete(1);
      Assert.That(tx.ContainsKey(1), Is.False);
      tx.Commit();
    }
    using var newReader = tree.BeginReadOnlyTransaction();
    Assert.That(Read(oldReader, 1), Is.EqualTo(10));
    Assert.That(Read(newReader, 1), Is.Null);
    Assert.That(Scan(newReader), Is.EqualTo(new[] { KeyValuePair.Create(2, 20) }));
    Assert.That(Scan(oldReader).Select(x => x.Key), Is.EqualTo(new[] { 1, 2 }));
  }

  [Test]
  public void RollbackAndDisposeDiscardWrites()
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    Write(tree, (1, 10));
    using (var tx = tree.BeginTransaction())
    {
      tx.Upsert(1, 11);
      tx.Upsert(2, 20);
      tx.Rollback();
      Assert.That(tx.State, Is.EqualTo(TransactionState.Aborted));
      Assert.Throws<TransactionAbortedException>(() => tx.TryGet(1, out _));
    }
    using (var tx = tree.BeginTransaction())
    {
      tx.Upsert(3, 30);
    }
    using var reader = tree.BeginReadOnlyTransaction();
    Assert.That(Scan(reader), Is.EqualTo(new[] { KeyValuePair.Create(1, 10) }));
    Assert.That(tree.Maintenance.ActiveTransactionCount, Is.EqualTo(1));
  }

  [Test]
  public void ConcurrentWritesToTheSameKeyConflict(
      [Values] TransactionIsolationLevel isolationLevel)
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    using var first = tree.BeginTransaction(isolationLevel);
    using var second = tree.BeginTransaction(isolationLevel);
    first.Upsert(1, 1);
    second.Upsert(1, 2);
    first.Commit();
    Assert.That(second.TryCommit(), Is.False);
    Assert.That(second.State, Is.EqualTo(TransactionState.Aborted));
    Assert.That(ReadCommitted(tree, 1), Is.EqualTo(1));

    // A write after the conflicting commit fails at once.
    using var third = tree.BeginTransaction(isolationLevel);
    using var fourth = tree.BeginTransaction(isolationLevel);
    third.Upsert(1, 3);
    third.Commit();
    Assert.Throws<TransactionConflictException>(() => fourth.Upsert(1, 4));
    Assert.That(fourth.State, Is.EqualTo(TransactionState.Aborted));
    Assert.That(ReadCommitted(tree, 1), Is.EqualTo(3));
  }

  [Test]
  public void WriteSkewIsRejectedOnlyBySerializable(
      [Values] TransactionIsolationLevel isolationLevel)
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    // Invariant: at least one of the two doctors stays on call.
    Write(tree, (1, 1), (2, 1));
    using var a = tree.BeginTransaction(isolationLevel);
    using var b = tree.BeginTransaction(isolationLevel);
    Assert.That(Read(a, 1) + Read(a, 2), Is.EqualTo(2));
    Assert.That(Read(b, 1) + Read(b, 2), Is.EqualTo(2));
    a.Upsert(1, 0);
    b.Upsert(2, 0);
    a.Commit();
    var isCommitted = b.TryCommit();
    Assert.That(isCommitted, Is.EqualTo(isolationLevel == TransactionIsolationLevel.Snapshot));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void SerializableRangeReadsDetectPhantoms(bool reverse)
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    Write(tree, (10, 1), (20, 1), (30, 1));

    // The scanner reads keys 10..20 and stops there.
    using var scanner = tree.BeginTransaction();
    using (var iterator = reverse ? scanner.CreateReverseIterator() : scanner.CreateIterator())
    {
      iterator.Seek(reverse ? 20 : 10);
      Assert.That(iterator.Next(), Is.True);
      Assert.That(iterator.Next(), Is.True);
      Assert.That(iterator.CurrentKey, Is.EqualTo(reverse ? 10 : 20));
    }
    scanner.Upsert(100, 1);

    // An insert outside the scanned range does not conflict.
    Write(tree, (reverse ? 5 : 25, 1));
    using (var scannerCopy = tree.BeginTransaction())
    {
      // A new key inside the scanned range is a phantom.
      scannerCopy.Upsert(15, 1);
      scannerCopy.Commit();
    }
    Assert.That(scanner.TryCommit(), Is.False);
  }

  [Test]
  public void ExhaustedScanProtectsTheRestOfTheKeySpace()
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    Write(tree, (1, 1));
    using var scanner = tree.BeginTransaction();
    Assert.That(Scan(scanner).Count, Is.EqualTo(1));
    scanner.Upsert(0, 0);
    Write(tree, (1000, 1));
    Assert.That(scanner.TryCommit(), Is.False);
  }

  [Test]
  public void SnapshotRangeReadsIgnorePhantoms()
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    Write(tree, (10, 1), (20, 1));
    using var scanner = tree.BeginTransaction(TransactionIsolationLevel.Snapshot);
    Assert.That(Scan(scanner).Count, Is.EqualTo(2));
    scanner.Upsert(100, 1);
    Write(tree, (15, 1));
    Assert.That(Scan(scanner).Select(x => x.Key), Is.EqualTo(new[] { 10, 20, 100 }));
    Assert.That(scanner.TryCommit(), Is.True);
  }

  [Test]
  public void IteratorsMergeVersionsInBothDirections()
  {
    using var tree = CreateFactory(mutableSegmentMaxItemCount: 7).OpenOrCreateTransactional();
    for (var round = 0; round < 5; ++round)
    {
      using var tx = tree.BeginTransaction();
      for (var key = 0; key < 20; ++key)
      {
        if ((key + round) % 3 == 0)
          tx.Delete(key);
        else
          tx.Upsert(key, key * 100 + round);
      }
      tx.Commit();
    }
    using var pending = tree.BeginTransaction();
    pending.Upsert(5, -1);
    pending.Upsert(50, -1);

    var expected = Enumerable.Range(0, 20)
        .Where(key => (key + 4) % 3 != 0)
        .Select(key => KeyValuePair.Create(key, key * 100 + 4))
        .ToList();
    using var reader = tree.BeginReadOnlyTransaction();
    Assert.That(Scan(reader), Is.EqualTo(expected));
    expected.Reverse();
    Assert.That(Scan(reader, reverse: true), Is.EqualTo(expected));

    var own = Scan(pending);
    Assert.That(own.Single(x => x.Key == 5).Value, Is.EqualTo(-1));
    Assert.That(own[^1], Is.EqualTo(KeyValuePair.Create(50, -1)));
    Assert.That(Scan(pending, reverse: true)[0], Is.EqualTo(KeyValuePair.Create(50, -1)));
  }

  [Test]
  public void SeekPositionsBothDirections()
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    Write(tree, (10, 1), (20, 2), (30, 3));
    using var tx = tree.BeginReadOnlyTransaction();
    using var forward = tx.CreateIterator();
    forward.Seek(15);
    Assert.That(forward.Next(), Is.True);
    Assert.That(forward.CurrentKey, Is.EqualTo(20));
    using var reverse = tx.CreateReverseIterator();
    reverse.Seek(25);
    Assert.That(reverse.Next(), Is.True);
    Assert.That(reverse.CurrentKey, Is.EqualTo(20));
    Assert.That(reverse.Next(), Is.True);
    Assert.That(reverse.CurrentKey, Is.EqualTo(10));
    Assert.That(reverse.Next(), Is.False);
  }

  [Test]
  public void ReadOnlyTransactionsCannotWrite()
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    using var tx = tree.BeginReadOnlyTransaction();
    Assert.That(tx.TransactionId, Is.Zero);
    Assert.Throws<TransactionIsReadOnlyException>(() => tx.Upsert(1, 1));
    Assert.That(tx.TryCommit(), Is.True);
    Assert.That(tx.CommitTimestamp, Is.EqualTo(tx.SnapshotTimestamp));
  }

  [Test]
  public void CommittedTransactionsSurviveReopenAndUncommittedOnesDoNot()
  {
    long lastId;
    using (var tree = CreateFactory().OpenOrCreateTransactional())
    {
      Write(tree, (1, 10), (2, 20));
      // Closing the tree without ending the transaction, as a crash would.
      var open = tree.BeginTransaction();
      open.Upsert(2, 99);
      open.Upsert(3, 30);
      lastId = open.TransactionId;
    }
    using (var tree = CreateFactory().OpenTransactional())
    {
      Assert.That(tree.Maintenance.AbortedTransactionCount, Is.EqualTo(1));
      using var reader = tree.BeginReadOnlyTransaction();
      Assert.That(Scan(reader), Is.EqualTo(new[]
      {
        KeyValuePair.Create(1, 10),
        KeyValuePair.Create(2, 20),
      }));
      using var tx = tree.BeginTransaction();
      Assert.That(tx.TransactionId, Is.GreaterThan(lastId));
      tx.Upsert(3, 33);
      tx.Commit();
    }
    using (var tree = CreateFactory().OpenTransactional())
    {
      Assert.That(ReadCommitted(tree, 3), Is.EqualTo(33));
      Assert.That(ReadCommitted(tree, 2), Is.EqualTo(20));
    }
  }

  [Test]
  public void ReopenAfterMergeKeepsCommittedData()
  {
    using (var tree = CreateFactory(mutableSegmentMaxItemCount: 10).OpenOrCreateTransactional())
    {
      for (var i = 0; i < 50; ++i)
        Write(tree, (i, i));
      MergeAll(tree);
      Write(tree, (0, -1));
    }
    using (var tree = CreateFactory(mutableSegmentMaxItemCount: 10).OpenTransactional())
    {
      using var reader = tree.BeginReadOnlyTransaction();
      var records = Scan(reader);
      Assert.That(records.Count, Is.EqualTo(50));
      Assert.That(records[0], Is.EqualTo(KeyValuePair.Create(0, -1)));
      Assert.That(records[49], Is.EqualTo(KeyValuePair.Create(49, 49)));
    }
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void MergeDropsVersionsNoSnapshotNeeds(DiskSegmentMode diskSegmentMode)
  {
    using var tree = CreateFactory(diskSegmentMode: diskSegmentMode).OpenOrCreateTransactional();
    for (var i = 0; i < 100; ++i)
      Write(tree, (i, 0));
    MergeAll(tree);

    using var oldReader = tree.BeginReadOnlyTransaction();
    for (var version = 1; version <= 5; ++version)
      Write(tree, (7, version), (8, version));
    MergeAll(tree);
    // The old snapshot still needs the version it sees.
    Assert.That(Read(oldReader, 7), Is.EqualTo(0));
    Assert.That(CountStoredVersions(tree, 7), Is.EqualTo(2));
    oldReader.Dispose();

    using (var tx = tree.BeginTransaction())
    {
      tx.Delete(8);
      tx.Commit();
    }
    MergeAll(tree);
    Assert.That(CountStoredVersions(tree, 7), Is.EqualTo(1));
    Assert.That(CountStoredVersions(tree, 8), Is.EqualTo(0), "a delete with nothing older is dropped");
    using var reader = tree.BeginReadOnlyTransaction();
    Assert.That(Read(reader, 7), Is.EqualTo(5));
    Assert.That(Read(reader, 8), Is.Null);
    var records = Scan(reader);
    Assert.That(records.Count, Is.EqualTo(99));
    Assert.That(records.Where(x => x.Key != 7).All(x => x.Value == 0), Is.True);
  }

  [Test]
  public void RolledBackVersionsAreForgottenAfterMerge()
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    Write(tree, (1, 1));
    using (var tx = tree.BeginTransaction())
    {
      tx.Upsert(1, 2);
      tx.Upsert(2, 2);
      tx.Rollback();
    }
    Assert.That(tree.Maintenance.AbortedTransactionCount, Is.EqualTo(1));
    MergeAll(tree);
    Assert.That(tree.Maintenance.AbortedTransactionCount, Is.Zero);
    Assert.That(CountStoredVersions(tree, 1), Is.EqualTo(1));
    Assert.That(CountStoredVersions(tree, 2), Is.Zero);
    Assert.That(ReadCommitted(tree, 1), Is.EqualTo(1));
  }

  [Test]
  public void BottomSegmentsKeepSnapshotsAndRollbacksCorrect()
  {
    using var tree = CreateFactory()
        .Configure(options => options.DiskSegmentMaxItemCount = 40)
        .OpenOrCreateTransactional();
    var expected = new Dictionary<int, int>();
    for (var round = 0; round < 6; ++round)
    {
      using (var tx = tree.BeginTransaction())
      {
        for (var key = 0; key < 50; ++key)
        {
          tx.Upsert(key, round);
          expected[key] = round;
        }
        tx.Commit();
      }
      // A rollback whose versions reach the disk segment before it rolls back.
      var rolledBack = tree.BeginTransaction();
      for (var key = 0; key < 50; key += 5)
        rolledBack.Upsert(key, -1);
      MergeAll(tree);
      rolledBack.Rollback();
      MergeAll(tree);
    }
    var maintenance = tree.Maintenance.ZoneTree.Maintenance;
    Assert.That(maintenance.BottomSegments.Count, Is.GreaterThan(1));
    maintenance.StartBottomSegmentsMergeOperation(0, int.MaxValue)?.Join();

    using var reader = tree.BeginReadOnlyTransaction();
    Assert.That(Scan(reader), Is.EqualTo(expected.OrderBy(x => x.Key).ToList()));
    Assert.That(Scan(reader, reverse: true).Count, Is.EqualTo(50));
    Assert.That(CountStoredVersions(tree, 3), Is.EqualTo(1), "the bottom merge keeps one version");
  }

  [Test]
  public void LargeTransactionSpansSegmentMoves([Values] bool commit)
  {
    const int count = 5000;
    using var tree = CreateFactory(mutableSegmentMaxItemCount: 200).OpenOrCreateTransactional();
    using var maintainer = tree.CreateMaintainer();
    using (var tx = tree.BeginTransaction())
    {
      for (var i = 0; i < count; ++i)
        tx.Upsert(i, i);
      Assert.That(Read(tx, 0), Is.EqualTo(0));
      Assert.That(Scan(tx).Count, Is.EqualTo(count));
      if (commit)
        tx.Commit();
      else
        tx.Rollback();
    }
    maintainer.WaitForBackgroundThreads();
    using var reader = tree.BeginReadOnlyTransaction();
    Assert.That(Scan(reader).Count, Is.EqualTo(commit ? count : 0));
    Assert.That(tree.Maintenance.ConflictIndexCount, Is.Zero, "no active snapshot needs the commit");
  }

  [Test]
  public async Task CommitDurableAsyncCommits()
  {
    using var tree = CreateFactory(walMode: WriteAheadLogMode.AsyncCompressed).OpenOrCreateTransactional();
    tree.AutoSyncInterval = TimeSpan.FromMilliseconds(10);
    using (var tx = tree.BeginTransaction())
    {
      tx.Upsert(1, 1);
      await tx.CommitDurableAsync();
      Assert.That(tx.State, Is.EqualTo(TransactionState.Committed));
    }
    tree.AutoSyncInterval = TimeSpan.Zero;
    Assert.That(ReadCommitted(tree, 1), Is.EqualTo(1));
  }

  [Test]
  public void ConcurrentTransfersKeepTheTotal(
      [Values] TransactionIsolationLevel isolationLevel)
  {
    const int accounts = 20;
    const int initialBalance = 100;
    using var tree = CreateFactory(mutableSegmentMaxItemCount: 300).OpenOrCreateTransactional();
    using var maintainer = tree.CreateMaintainer();
    Write(tree, Enumerable.Range(0, accounts).Select(x => (x, initialBalance)).ToArray());

    var committed = 0;
    var inconsistentSnapshots = 0;
    var writers = Enumerable.Range(0, 4).Select(seed => Task.Run(() =>
    {
      var random = new Random(seed);
      for (var i = 0; i < 300; ++i)
      {
        var from = random.Next(accounts);
        var to = (from + 1 + random.Next(accounts - 1)) % accounts;
        try
        {
          using var tx = tree.BeginTransaction(isolationLevel);
          tx.TryGet(from, out var fromBalance);
          tx.TryGet(to, out var toBalance);
          tx.Upsert(from, fromBalance - 1);
          tx.Upsert(to, toBalance + 1);
          if (tx.TryCommit())
            Interlocked.Increment(ref committed);
        }
        catch (TransactionConflictException)
        {
        }
      }
    }));
    var reader = Task.Run(() =>
    {
      for (var i = 0; i < 100; ++i)
      {
        using var tx = tree.BeginReadOnlyTransaction();
        if (Scan(tx).Sum(x => x.Value) != accounts * initialBalance)
          Interlocked.Increment(ref inconsistentSnapshots);
      }
    });
    Task.WaitAll(writers.Append(reader).ToArray());
    maintainer.WaitForBackgroundThreads();

    Assert.That(committed, Is.GreaterThan(0));
    Assert.That(inconsistentSnapshots, Is.Zero);
    using var final = tree.BeginReadOnlyTransaction();
    Assert.That(Scan(final).Sum(x => x.Value), Is.EqualTo(accounts * initialBalance));
    Assert.That(tree.Maintenance.ActiveTransactionCount, Is.EqualTo(1));
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void MergesDuringTransfersKeepSnapshotsConsistent(DiskSegmentMode diskSegmentMode)
  {
    const int accounts = 50;
    const int initialBalance = 1000;
    using var tree = CreateFactory(
        mutableSegmentMaxItemCount: 100,
        diskSegmentMode: diskSegmentMode).OpenOrCreateTransactional();
    Write(tree, Enumerable.Range(0, accounts).Select(x => (x, initialBalance)).ToArray());

    var stop = 0;
    var inconsistentSnapshots = 0;
    var merger = Task.Run(() =>
    {
      while (Volatile.Read(ref stop) == 0)
        MergeAll(tree);
    });
    var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
    {
      while (Volatile.Read(ref stop) == 0)
      {
        using var tx = tree.BeginReadOnlyTransaction();
        var first = Scan(tx).Sum(x => x.Value);
        // A long reader keeps its snapshot across merges.
        Thread.Sleep(1);
        var second = Scan(tx, reverse: true).Sum(x => x.Value);
        if (first != accounts * initialBalance || second != first)
          Interlocked.Increment(ref inconsistentSnapshots);
      }
    })).ToArray();
    var writers = Enumerable.Range(0, 4).Select(seed => Task.Run(() =>
    {
      var random = new Random(seed);
      for (var i = 0; i < 500; ++i)
      {
        var from = random.Next(accounts);
        var to = (from + 1 + random.Next(accounts - 1)) % accounts;
        try
        {
          using var tx = tree.BeginTransaction();
          tx.TryGet(from, out var fromBalance);
          tx.TryGet(to, out var toBalance);
          tx.Upsert(from, fromBalance - 1);
          tx.Upsert(to, toBalance + 1);
          tx.TryCommit();
        }
        catch (TransactionConflictException)
        {
        }
      }
    })).ToArray();
    Task.WaitAll(writers);
    Volatile.Write(ref stop, 1);
    Task.WaitAll(readers.Append(merger).ToArray());

    Assert.That(inconsistentSnapshots, Is.Zero);
    MergeAll(tree);
    using var final = tree.BeginReadOnlyTransaction();
    var records = Scan(final);
    Assert.That(records.Count, Is.EqualTo(accounts));
    Assert.That(records.Sum(x => x.Value), Is.EqualTo(accounts * initialBalance));
    Assert.That(tree.Maintenance.AbortedTransactionCount, Is.Zero);
    // With no older snapshot left, a merge keeps one version per key.
    Assert.That(Enumerable.Range(0, accounts).All(x => CountStoredVersions(tree, x) == 1), Is.True);
  }

  [Test]
  public void ConcurrentIncrementsAreNotLost([Values] TransactionIsolationLevel isolationLevel)
  {
    using var tree = CreateFactory(mutableSegmentMaxItemCount: 1000).OpenOrCreateTransactional();
    using var maintainer = tree.CreateMaintainer();
    var committed = 0;
    Parallel.For(0, 2000, _ =>
    {
      while (true)
      {
        try
        {
          using var tx = tree.BeginTransaction(isolationLevel);
          tx.TryGet(0, out var counter);
          tx.Upsert(0, counter + 1);
          if (tx.TryCommit())
          {
            Interlocked.Increment(ref committed);
            return;
          }
        }
        catch (TransactionConflictException)
        {
        }
      }
    });
    maintainer.WaitForBackgroundThreads();
    Assert.That(committed, Is.EqualTo(2000));
    Assert.That(ReadCommitted(tree, 0), Is.EqualTo(2000));
  }

  [Test]
  public void ConcurrentInsertsRespectARangeLimit([Values] TransactionIsolationLevel isolationLevel)
  {
    // Each transaction inserts a key into [0, 1000) only while the range has fewer than
    // `limit` keys. Only phantom protection keeps concurrent inserts within the limit.
    const int limit = 10;
    using var tree = CreateFactory().OpenOrCreateTransactional();
    using var start = new Barrier(8);
    Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
    {
      start.SignalAndWait();
      for (var i = 0; i < 20; ++i)
      {
        try
        {
          using var tx = tree.BeginTransaction(isolationLevel);
          using (var iterator = tx.CreateIterator())
          {
            var count = 0;
            iterator.Seek(0);
            while (iterator.Next() && iterator.CurrentKey < 1000)
              ++count;
            if (count >= limit)
              return;
          }
          tx.Upsert(worker * 100 + i, 1);
          Thread.Yield();
          tx.TryCommit();
        }
        catch (TransactionConflictException)
        {
        }
      }
    });
    using var reader = tree.BeginReadOnlyTransaction();
    var total = Scan(reader).Count;
    if (isolationLevel == TransactionIsolationLevel.Serializable)
      Assert.That(total, Is.EqualTo(limit));
    else
      Assert.That(total, Is.GreaterThanOrEqualTo(limit));
  }

  [Test]
  public void ReadOnlySnapshotsDoNotKeepTheConflictIndex()
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    using var report = tree.BeginReadOnlyTransaction();
    for (var i = 0; i < 10; ++i)
      Write(tree, (i, i));
    Assert.That(tree.Maintenance.ConflictIndexCount, Is.Zero);

    using var writer = tree.BeginTransaction();
    Write(tree, (100, 1));
    Assert.That(tree.Maintenance.ConflictIndexCount, Is.EqualTo(1),
        "an older writing transaction can still conflict with the key");
    writer.Rollback();
    Assert.That(tree.Maintenance.ConflictIndexCount, Is.Zero);
    Assert.That(Scan(report), Is.Empty, "the report keeps its snapshot");
  }

  [Test]
  public void IteratorsEndWithTheTransaction()
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    Write(tree, (1, 1));
    var tx = tree.BeginTransaction();
    var iterator = tx.CreateIterator();
    Assert.That(iterator.Next(), Is.True);
    tx.Commit();
    Assert.Throws<TransactionAlreadyCommittedException>(() => iterator.Next());
    iterator.Dispose();
    tx.Dispose();
    Assert.That(tree.Maintenance.ActiveTransactionCount, Is.Zero);
  }

  sealed class FailingValueSerializer : ISerializer<int>
  {
    public bool FailNext;

    public Memory<byte> Serialize(in int value)
    {
      if (FailNext)
      {
        FailNext = false;
        throw new InvalidDataException("Injected serialization failure.");
      }
      return BitConverter.GetBytes(value);
    }

    public int Deserialize(Memory<byte> bytes) => BitConverter.ToInt32(bytes.Span);
  }

  [Test]
  public void FailedWriteRollsBackTheTransaction()
  {
    var serializer = new FailingValueSerializer();
    using (var tree = CreateFactory().SetValueSerializer(serializer).OpenOrCreateTransactional())
    {
      Write(tree, (1, 10));
      using var tx = tree.BeginTransaction();
      serializer.FailNext = true;
      Assert.Throws<InvalidDataException>(() => tx.Upsert(1, 20));
      Assert.That(tx.State, Is.EqualTo(TransactionState.Aborted));
      Assert.Throws<TransactionAbortedException>(() => tx.Commit());
      Assert.That(ReadCommitted(tree, 1), Is.EqualTo(10));
    }
    using var reopened = CreateFactory().SetValueSerializer(serializer).OpenTransactional();
    Assert.That(ReadCommitted(reopened, 1), Is.EqualTo(10));
  }

  sealed class BigEndianInt32Serializer : ISerializer<int>
  {
    public Memory<byte> Serialize(in int value)
    {
      var bytes = new byte[4];
      System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
      return bytes;
    }

    public int Deserialize(Memory<byte> bytes)
        => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.Span);
  }

  [Test]
  public void OpeningWithAnotherSerializerIsRejected()
  {
    using (var tree = CreateFactory().SetValueSerializer(new BigEndianInt32Serializer())
        .OpenOrCreateTransactional())
      Write(tree, (1, 1));
    Assert.Throws<TransactionalTreeSchemaMismatchException>(
        () => CreateFactory().OpenTransactional().Dispose());
    using var reopened = CreateFactory().SetValueSerializer(new BigEndianInt32Serializer())
        .OpenTransactional();
    Assert.That(ReadCommitted(reopened, 1), Is.EqualTo(1));
  }

  [Test]
  public void TransactionalTreeRejectsReadOnlyMode()
  {
    using var tree = CreateFactory().OpenOrCreateTransactional();
    Write(tree, (1, 1));
    tree.IsReadOnly = true;
    Assert.Throws<ZoneTreeIsReadOnlyException>(() => tree.BeginTransaction());
    Assert.That(ReadCommitted(tree, 1), Is.EqualTo(1));
  }
}
