using Transactions.Benchmark.Engines;
using Transactions.Benchmark.Measurement;

namespace Transactions.Benchmark.Scenarios;

/// <summary>
/// Background writers that move one unit between two keys of the same bucket,
/// so every bucket keeps a sum of zero.
/// </summary>
static class BucketWriters
{
  public const int BucketSize = 100;
  public const int WriterCount = 2;

  public static long Buckets(long keys) => Math.Max(1, keys / BucketSize);

  public static void Move(Worker worker, long keys)
  {
    var bucket = worker.Random.NextInt64(Buckets(keys));
    var first = bucket * BucketSize + worker.Random.Next(BucketSize / 2);
    var second = bucket * BucketSize + BucketSize / 2 + worker.Random.Next(BucketSize / 2);
    while (!worker.IsStopping)
    {
      using var transaction = worker.Session.BeginWrite();
      var firstCounter = transaction.TryGetForUpdate(first, out var value) ? ValueFactory.Counter(value) : 0;
      var secondCounter = transaction.TryGetForUpdate(second, out value) ? ValueFactory.Counter(value) : 0;
      transaction.Put(first, worker.NewValue(firstCounter + 1));
      transaction.Put(second, worker.NewValue(secondCounter - 1));
      if (worker.Commit(transaction))
        return;
    }
  }
}

/// <summary>
/// Read-only transactions of several point reads, with or without writers.
/// </summary>
public sealed class SnapshotPointScenario(string id, string title, string useCase, bool withWriters, long job)
    : Scenario(id, "snapshot-reads", title, useCase, withWriters
        ? "The counters add up to the commits of the writers."
        : "Every read finds its key.")
{
  const string Readers = "readers";
  const string Writers = "writers";
  const int ReadsPerTransaction = 10;
  const int WriterCount = 2;

  public override void Load(ScenarioContext context) =>
      context.LoadCounters(0, context.Keys);

  public override IReadOnlyList<Role> CreateRoles(ScenarioContext context)
  {
    var keys = context.Keys;
    var roles = new List<Role>
    {
      new(Readers, context.Workers, worker =>
      {
        using var transaction = worker.Session.BeginRead();
        for (var i = 0; i < ReadsPerTransaction; i++)
        {
          var found = transaction.TryGet(worker.Random.NextInt64(keys), out _);
          if (worker.AnalyzeInvariants && !found)
            worker.AddAnomalies();
        }
        worker.AddRows(ReadsPerTransaction);
      }, context.Job(job))
    };
    if (withWriters)
      roles.Add(new(Writers, WriterCount, worker => Rmw.Run(worker, worker.Random.NextInt64(keys))));
    return roles;
  }

  public override void Verify(ScenarioContext context)
  {
    var (sum, count) = context.ScanSum(0, context.Keys - 1);
    var commits = context.TotalCommits(Writers);
    context.Check(count == context.Keys, $"expected {context.Keys} keys, found {count}");
    context.Check(sum == commits, $"counters add up to {sum}, but the writers committed {commits}");
  }
}

/// <summary>
/// Read-only range scans, checked against the bucket invariant the writers keep.
/// </summary>
public sealed class SnapshotRangeScenario(string id, string title, string useCase, bool fullScan)
    : Scenario(id, "snapshot-reads", title, useCase, "Every scan sees a sum of zero and every key of its range, while writers change them.")
{
  const string Readers = "readers";
  const string Writers = "writers";
  const long RangeJob = 100_000;
  const long FullScans = 100;

  public override bool ReportsRows => true;

  public override void Load(ScenarioContext context) =>
      context.LoadCounters(0, context.Keys);

  public override IReadOnlyList<Role> CreateRoles(ScenarioContext context)
  {
    var keys = BucketWriters.Buckets(context.Keys) * BucketWriters.BucketSize;
    var job = fullScan ? context.FixedJob(FullScans) : context.Job(RangeJob);
    return
    [
      new(Readers, context.Workers, worker => Scan(worker, keys), job),
      new(Writers, BucketWriters.WriterCount, worker => BucketWriters.Move(worker, keys)),
    ];
  }

  void Scan(Worker worker, long keys)
  {
    var from = 0L;
    var to = keys - 1;
    if (!fullScan)
    {
      from = worker.Random.NextInt64(BucketWriters.Buckets(keys)) * BucketWriters.BucketSize;
      to = from + BucketWriters.BucketSize - 1;
    }
    using var transaction = worker.Session.BeginRead();
    var (sum, count) = ScenarioContext.Scan(transaction, from, to, worker.AnalyzeInvariants);
    worker.AddRows(count);
    if (worker.AnalyzeInvariants && (sum != 0 || count != to - from + 1))
      worker.AddAnomalies();
  }

  public override void Verify(ScenarioContext context)
  {
    var keys = BucketWriters.Buckets(context.Keys) * BucketWriters.BucketSize;
    var (sum, count) = context.ScanSum(0, keys - 1);
    context.Check(count == keys, $"expected {keys} keys, found {count}");
    context.Check(sum == 0, $"the buckets add up to {sum}, expected 0");
  }
}

/// <summary>
/// One snapshot stays open for the whole run while the workers write.
/// </summary>
public sealed class LongReaderScenario()
    : Scenario(
        "long-reader",
        "snapshot-reads",
        "Writes under a long-lived snapshot",
        "Reports, exports and backups: what an old snapshot costs the writers, and whether it stays exact.",
        "The snapshot reads the same data at its end as at its start, and the counters add up to the commits.")
{
  const string Writers = "writers";
  const string Snapshot = "long-snapshot";
  const long Job = 100_000;

  public override void Load(ScenarioContext context) =>
      context.LoadCounters(0, context.Keys);

  public override IReadOnlyList<Role> CreateRoles(ScenarioContext context)
  {
    var keys = context.Keys;
    return
    [
      new(Writers, context.Workers, worker => Rmw.Run(worker, worker.Random.NextInt64(keys)), context.Job(Job)),
      new(Snapshot, 1, worker => HoldSnapshot(worker, keys)),
    ];
  }

  static void HoldSnapshot(Worker worker, long keys)
  {
    using var transaction = worker.Session.BeginRead();
    var (firstSum, firstCount) = ScenarioContext.Scan(transaction, 0, keys - 1, worker.AnalyzeInvariants);
    while (!worker.IsStopping)
      Thread.Sleep(50);
    var (lastSum, lastCount) = ScenarioContext.Scan(transaction, 0, keys - 1, worker.AnalyzeInvariants);
    if (worker.AnalyzeInvariants && (firstSum != lastSum || firstCount != lastCount))
      worker.AddAnomalies();
  }

  public override void Verify(ScenarioContext context)
  {
    var (sum, _) = context.ScanSum(0, context.Keys - 1);
    var commits = context.TotalCommits(Writers);
    context.Check(sum == commits, $"counters add up to {sum}, but the writers committed {commits}");
  }
}
