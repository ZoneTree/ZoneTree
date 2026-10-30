using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Transactional;

namespace ZoneTree.UnitTests;

/// <summary>
/// Runs random interleavings of transactions, segment moves and merges on one thread,
/// and checks every result against an in-memory model of the committed state.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every read and scan equals the snapshot of the transaction plus its own writes.</item>
/// <item>A commit never overwrites a key committed after its snapshot (first committer wins).</item>
/// <item>A commit that wrote never follows a commit to a key it read for update after its snapshot.</item>
/// <item>A serializable commit read nothing that changed before it committed: every key and
/// every scanned range it observed still holds the observed values at its commit.</item>
/// <item>The committed state survives merges, garbage collection and reopens.</item>
/// <item>After a power loss, the tree recovers the state after a prefix of the commits
/// in commit order, and the prefix includes every synced commit.</item>
/// </list>
/// </remarks>
public sealed class MvccZoneTreeModelTests
{
  const int KeyCount = 24;

  const int StepCount = 3000;

  static ZoneTreeFactory<int, int> CreateFactory(
      IFileStreamProvider provider,
      DiskSegmentMode diskSegmentMode,
      WriteAheadLogMode walMode)
  {
    return new ZoneTreeFactory<int, int>(provider)
        .SetDataDirectory("model")
        .SetLogger(new ConsoleLogger(LogLevel.Error))
        .Configure(options =>
        {
          options.AllowUnsafeOptionValues = true;
          options.DiskSegmentMaxItemCount = 120;
        })
        .SetMutableSegmentMaxItemCount(48)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DiskSegmentMode = diskSegmentMode;
          options.MinimumRecordCount = 2;
          options.MaximumRecordCount = 8;
        })
        .ConfigureWriteAheadLogOptions(options =>
        {
          options.WriteAheadLogMode = walMode;
          options.CompressionBlockSize = 256;
        });
  }

  sealed class ScanRecord
  {
    public bool HasLow;

    public int Low;

    public bool HasHigh;

    public int High;

    public HashSet<int> OwnKeysAtScan;

    public Dictionary<int, int> Observed;
  }

  sealed class ModelTransaction
  {
    public IReadTransaction<int, int> Transaction;

    /// <summary>
    /// The transaction, or null when it is read-only.
    /// </summary>
    public ITransaction<int, int> Writer;

    public Dictionary<int, int?> Snapshot;

    public long SnapshotTimestamp;

    /// <summary>
    /// The own writes; null is a delete.
    /// </summary>
    public readonly Dictionary<int, int?> Writes = new();

    /// <summary>
    /// The reads of keys the transaction had not written: key and observed value.
    /// </summary>
    public readonly List<(int Key, int? Value)> Reads = new();

    public readonly HashSet<int> ReadsForUpdate = new();

    public readonly List<ScanRecord> Scans = new();

    public long TransactionId => Writer?.TransactionId ?? 0;

    public bool IsSerializable =>
        Writer?.IsolationLevel == TransactionIsolationLevel.Serializable;

    public int? Expected(int key)
        => Writes.TryGetValue(key, out var own) ? own : Snapshot.GetValueOrDefault(key);
  }

  /// <summary>
  /// Applies random operations to a tree and to the model, and checks every result.
  /// </summary>
  sealed class HistoryRunner(Random random)
  {
    public ITransactionalZoneTree<int, int> Tree;

    readonly Dictionary<int, int?> Committed = new();

    readonly Dictionary<int, long> LastCommitOfKey = new();

    readonly List<ModelTransaction> Active = new();

    /// <summary>
    /// The committed state after each commit that wrote; the first one is empty.
    /// </summary>
    public readonly List<Dictionary<int, int>> CommittedStates = new() { new() };

    int NextValue = 1;

    public void Step()
    {
      var choice = random.Next(100);
      if (Active.Count == 0 || choice < 10)
      {
        if (Active.Count < 6)
          Active.Add(Begin());
        return;
      }
      var model = Active[random.Next(Active.Count)];
      if (choice < 35)
        Read(model);
      else if (choice < 40)
      {
        if (model.Writer != null)
          ReadForUpdate(model);
      }
      else if (choice < 65)
      {
        if (model.Writer != null)
          Write(model);
      }
      else if (choice < 70)
        Scan(model);
      else if (choice < 75)
        ReadRange(model);
      else if (choice < 88)
        Commit(model);
      else if (choice < 92)
      {
        Active.Remove(model);
        if (model.Writer != null)
          model.Writer.Rollback();
        else
          model.Transaction.Dispose();
      }
      else if (choice < 96)
        Tree.Maintenance.ZoneTree.Maintenance.MoveMutableSegmentForward();
      else if (choice < 99)
        Tree.Maintenance.ZoneTree.Maintenance.StartMergeOperation()?.Join();
      else
        Tree.Maintenance.ZoneTree.Maintenance.StartBottomSegmentsMergeOperation(0, int.MaxValue)?.Join();
      Assert.That(Tree.Maintenance.ActiveTransactionCount, Is.EqualTo(Active.Count));
    }

    public void EndActiveTransactions()
    {
      foreach (var model in Active)
        model.Transaction.Dispose();
      Active.Clear();
    }

    ModelTransaction Begin()
    {
      var writer = random.Next(4) == 0 ?
          null :
          Tree.BeginTransaction(random.Next(2) == 0 ?
              TransactionIsolationLevel.Snapshot :
              TransactionIsolationLevel.Serializable);
      var transaction = writer ?? Tree.BeginReadOnlyTransaction();
      return new ModelTransaction
      {
        Transaction = transaction,
        Writer = writer,
        Snapshot = new Dictionary<int, int?>(Committed),
        SnapshotTimestamp = transaction.SnapshotTimestamp,
      };
    }

    void Read(ModelTransaction model)
    {
      var key = random.Next(KeyCount);
      var found = model.Transaction.TryGet(key, out var value);
      var expected = model.Expected(key);
      Assert.That(found ? value : (int?)null, Is.EqualTo(expected),
          $"read of key {key} in transaction {model.TransactionId}");
      if (!model.Writes.ContainsKey(key))
        model.Reads.Add((key, expected));
    }

    void ReadForUpdate(ModelTransaction model)
    {
      var key = random.Next(KeyCount);
      var found = model.Writer.TryGetForUpdate(key, out var value);
      var expected = model.Expected(key);
      Assert.That(found ? value : (int?)null, Is.EqualTo(expected),
          $"read for update of key {key} in transaction {model.TransactionId}");
      model.ReadsForUpdate.Add(key);
      if (!model.Writes.ContainsKey(key))
        model.Reads.Add((key, expected));
    }

    void ReadRange(ModelTransaction model)
    {
      var reverse = random.Next(2) == 0;
      var from = random.Next(KeyCount);
      var to = random.Next(KeyCount);
      var limit = random.Next(3) == 0 ? int.MaxValue : 1 + random.Next(6);
      var expected = Enumerable.Range(from, Math.Max(0, to - from + 1))
          .Select(key => (Key: key, Value: model.Expected(key)))
          .Where(x => x.Value.HasValue)
          .Select(x => KeyValuePair.Create(x.Key, x.Value.Value));
      if (reverse)
        expected = expected.Reverse();
      var expectedList = expected.Take(limit).ToList();

      var actual = model.Transaction.Range(from, to, reverse).Take(limit).ToList();
      Assert.That(actual, Is.EqualTo(expectedList),
          $"range [{from}, {to}] reverse={reverse} limit={limit}");
      if (from > to)
        return;

      // An enumeration to the end observed the whole range,
      // one stopped early up to its last key.
      var isComplete = actual.Count < limit;
      model.Scans.Add(new ScanRecord
      {
        OwnKeysAtScan = new HashSet<int>(model.Writes.Keys),
        Observed = actual.ToDictionary(x => x.Key, x => x.Value),
        HasLow = true,
        Low = isComplete || !reverse ? from : actual[^1].Key,
        HasHigh = true,
        High = isComplete || reverse ? to : actual[^1].Key,
      });
    }

    void Write(ModelTransaction model)
    {
      var key = random.Next(KeyCount);
      int? value = random.Next(4) == 0 ? null : NextValue++;
      var hadConflict = model.Writer.HasConflict;
      if (value.HasValue)
        model.Writer.Upsert(key, value.Value);
      else
        model.Writer.Delete(key);
      Assert.That(model.Writer.State, Is.EqualTo(TransactionState.Active));
      if (model.Writer.HasConflict)
      {
        // Only a key committed after the snapshot conflicts, and a conflicting
        // transaction stores no more writes.
        if (!hadConflict)
          Assert.That(LastCommitOfKey.GetValueOrDefault(key), Is.GreaterThan(model.SnapshotTimestamp));
        return;
      }
      model.Writes[key] = value;
    }

    void Scan(ModelTransaction model)
    {
      var reverse = random.Next(2) == 0;
      var hasSeek = random.Next(3) != 0;
      var seek = random.Next(KeyCount);
      var limit = random.Next(3) == 0 ? int.MaxValue : 1 + random.Next(6);
      var expected = Enumerable.Range(0, KeyCount)
          .Select(key => (Key: key, Value: model.Expected(key)))
          .Where(x => x.Value.HasValue)
          .Where(x => !hasSeek || (reverse ? x.Key <= seek : x.Key >= seek))
          .Select(x => KeyValuePair.Create(x.Key, x.Value.Value));
      if (reverse)
        expected = expected.Reverse();
      var expectedList = expected.Take(limit).ToList();

      using var iterator = reverse ?
          model.Transaction.CreateReverseIterator() :
          model.Transaction.CreateIterator();
      if (hasSeek)
        iterator.Seek(seek);
      var actual = new List<KeyValuePair<int, int>>();
      while (actual.Count < limit && iterator.Next())
        actual.Add(iterator.Current);
      Assert.That(actual, Is.EqualTo(expectedList),
          $"scan reverse={reverse} seek={(hasSeek ? seek : "first")} limit={limit}");

      // The scan observed every key between its start and its last key,
      // or up to the end when it ran out of keys.
      var exhausted = actual.Count < limit;
      var record = new ScanRecord
      {
        OwnKeysAtScan = new HashSet<int>(model.Writes.Keys),
        Observed = actual.ToDictionary(x => x.Key, x => x.Value),
      };
      if (reverse)
      {
        record.HasHigh = hasSeek;
        record.High = seek;
        record.HasLow = !exhausted;
        record.Low = exhausted ? 0 : actual[^1].Key;
      }
      else
      {
        record.HasLow = hasSeek;
        record.Low = seek;
        record.HasHigh = !exhausted;
        record.High = exhausted ? 0 : actual[^1].Key;
      }
      model.Scans.Add(record);
    }

    void Commit(ModelTransaction model)
    {
      Active.Remove(model);
      if (model.Writer == null)
      {
        model.Transaction.Dispose();
        return;
      }
      var hasConflict = model.Writer.HasConflict;
      if (!model.Writer.TryCommit())
      {
        Assert.That(model.Writer.State, Is.EqualTo(TransactionState.Aborted));
        return;
      }
      Assert.That(hasConflict, Is.False, "a transaction with a conflicting write committed");
      // A transaction that wrote nothing commits at its snapshot.
      if (model.Writes.Count == 0)
        return;
      foreach (var key in model.ReadsForUpdate)
      {
        Assert.That(LastCommitOfKey.GetValueOrDefault(key), Is.LessThanOrEqualTo(model.SnapshotTimestamp),
            $"key {key} read for update changed before the commit");
      }
      foreach (var key in model.Writes.Keys)
      {
        Assert.That(LastCommitOfKey.GetValueOrDefault(key), Is.LessThanOrEqualTo(model.SnapshotTimestamp),
            $"lost update of key {key}");
      }
      if (model.IsSerializable)
        CheckSerializableReads(model);
      var commitTimestamp = model.Writer.CommitTimestamp;
      foreach (var (key, value) in model.Writes)
      {
        Committed[key] = value;
        LastCommitOfKey[key] = commitTimestamp;
      }
      CommittedStates.Add(CommittedState());
    }

    void CheckSerializableReads(ModelTransaction model)
    {
      foreach (var (key, value) in model.Reads)
      {
        Assert.That(Committed.GetValueOrDefault(key), Is.EqualTo(value),
            $"key {key} changed between the read and the serializable commit");
      }
      foreach (var scan in model.Scans)
      {
        for (var key = 0; key < KeyCount; ++key)
        {
          if ((scan.HasLow && key < scan.Low) || (scan.HasHigh && key > scan.High) ||
              scan.OwnKeysAtScan.Contains(key))
            continue;
          int? observed = scan.Observed.TryGetValue(key, out var value) ? value : null;
          Assert.That(Committed.GetValueOrDefault(key), Is.EqualTo(observed),
              $"key {key} in a scanned range changed before the serializable commit");
        }
      }
    }

    public Dictionary<int, int> CommittedState()
        => Committed.Where(x => x.Value.HasValue).ToDictionary(x => x.Key, x => x.Value.Value);
  }

  static Dictionary<int, int> ReadAll(ITransactionalZoneTree<int, int> tree)
  {
    using var reader = tree.BeginReadOnlyTransaction();
    using var iterator = reader.CreateIterator();
    var records = new Dictionary<int, int>();
    while (iterator.Next())
      records.Add(iterator.CurrentKey, iterator.CurrentValue);
    return records;
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment, 1)]
  [TestCase(DiskSegmentMode.SingleDiskSegment, 2)]
  [TestCase(DiskSegmentMode.SingleDiskSegment, 3)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment, 4)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment, 5)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment, 6)]
  public void RandomHistoriesMatchTheModel(DiskSegmentMode diskSegmentMode, int seed)
  {
    var provider = new InMemoryFileStreamProvider();
    var random = new Random(seed);
    var runner = new HistoryRunner(random)
    {
      Tree = CreateFactory(provider, diskSegmentMode, WriteAheadLogMode.Sync).OpenOrCreateTransactional()
    };
    try
    {
      for (var step = 0; step < StepCount; ++step)
      {
        if (random.Next(100) == 0)
        {
          // A clean reopen ends the open transactions and keeps every commit.
          runner.EndActiveTransactions();
          runner.Tree.Dispose();
          runner.Tree = CreateFactory(provider, diskSegmentMode, WriteAheadLogMode.Sync)
              .OpenTransactional();
          Assert.That(ReadAll(runner.Tree), Is.EquivalentTo(runner.CommittedState()));
          continue;
        }
        runner.Step();
      }
      runner.EndActiveTransactions();
      Assert.That(ReadAll(runner.Tree), Is.EquivalentTo(runner.CommittedState()));
      runner.Tree.Maintenance.ZoneTree.Maintenance.MoveMutableSegmentForward();
      runner.Tree.Maintenance.ZoneTree.Maintenance.StartMergeOperation()?.Join();
      Assert.That(ReadAll(runner.Tree), Is.EquivalentTo(runner.CommittedState()));
    }
    finally
    {
      runner.Tree.Dispose();
    }
  }

  static IEnumerable<TestCaseData> PowerLossCases()
  {
    var modes = new[]
    {
      (WriteAheadLogMode.Sync, DiskSegmentMode.SingleDiskSegment),
      (WriteAheadLogMode.Sync, DiskSegmentMode.MultiPartDiskSegment),
      (WriteAheadLogMode.SyncCompressed, DiskSegmentMode.SingleDiskSegment),
      (WriteAheadLogMode.AsyncCompressed, DiskSegmentMode.MultiPartDiskSegment),
    };
    // One seed per mode caught a merge that dropped the only durable value only once in
    // four runs, so every mode runs with several seeds.
    var seed = 11;
    foreach (var (walMode, diskSegmentMode) in modes)
    {
      for (var i = 0; i < 3; ++i)
        yield return new TestCaseData(walMode, diskSegmentMode, seed++);
    }
  }

  [TestCaseSource(nameof(PowerLossCases))]
  public async Task PowerLossRecoversACommittedPrefix(
      WriteAheadLogMode walMode, DiskSegmentMode diskSegmentMode, int seed)
  {
    var provider = new PowerLossFileStreamProvider();
    var random = new Random(seed);
    var runner = new HistoryRunner(random)
    {
      Tree = CreateFactory(provider, diskSegmentMode, walMode).OpenOrCreateTransactional()
    };
    var syncedStateCount = 1;
    try
    {
      for (var step = 0; step < StepCount; ++step)
      {
        var choice = random.Next(100);
        if (choice < 2)
        {
          var stateCount = runner.CommittedStates.Count;
          await runner.Tree.SyncWALAsync();
          syncedStateCount = stateCount;
          continue;
        }
        if (choice < 4)
        {
          // The image keeps only what reached the disk. The tree recovers the state after
          // a prefix of the commits that includes every synced commit.
          var image = provider.SimulatePowerLoss();
          using var recovered = CreateFactory(image, diskSegmentMode, walMode).OpenTransactional();
          var state = ReadAll(recovered);
          var prefixLength = runner.CommittedStates.FindLastIndex(x =>
              x.Count == state.Count && !x.Except(state).Any()) + 1;
          Assert.That(prefixLength, Is.GreaterThan(0),
              "the recovered state is not the state after any prefix of the commits");
          Assert.That(prefixLength, Is.GreaterThanOrEqualTo(syncedStateCount),
              "a synced commit was lost");
          continue;
        }
        runner.Step();
      }
    }
    finally
    {
      runner.EndActiveTransactions();
      runner.Tree.Dispose();
    }
  }
}
