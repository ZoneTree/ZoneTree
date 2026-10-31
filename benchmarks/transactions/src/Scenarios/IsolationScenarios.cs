using Transactions.Benchmark.Engines;
using Transactions.Benchmark.Measurement;

namespace Transactions.Benchmark.Scenarios;

/// <summary>
/// The on-call rule: each group of two keeps someone on call. A transaction reads both
/// and takes one off only if both are on. With plain reads, two concurrent transactions
/// can each take a different one off: the write skew.
/// </summary>
public sealed class WriteSkewScenario(bool guarded)
    : Scenario(
        guarded ? "write-skew-guarded" : "write-skew",
        "isolation",
        guarded ? "Write skew with reads for update" : "Write skew with plain reads",
        guarded
            ? "A rule spans keys a transaction reads but does not write, and the application protects it with reads for update."
            : "A rule spans keys a transaction reads but does not write, and the application relies on the isolation level alone.",
        guarded
            ? "No transaction finds a group with nobody on call."
            : "None: the anomaly count shows which engines allow write skew.")
{
  const string RoleName = "transactions";
  const int Groups = 64;
  const long Job = 300_000;

  public override bool AnomaliesViolateInvariant => guarded;

  public override void Load(ScenarioContext context) =>
      context.LoadCounters(0, Groups * 2, counter: 1);

  public override IReadOnlyList<Role> CreateRoles(ScenarioContext context) =>
      [new(RoleName, context.Workers, Run, context.Job(Job))];

  void Run(Worker worker)
  {
    var group = worker.Random.Next(Groups);
    var first = group * 2L;
    var second = first + 1;
    var takeFirst = worker.Random.Next(2) == 0;
    while (!worker.IsStopping)
    {
      using var transaction = worker.Session.BeginWrite();
      var firstOn = Read(transaction, first);
      var secondOn = Read(transaction, second);
      var bothOff = !transaction.HasConflict && firstOn == 0 && secondOn == 0;
      if (firstOn + secondOn == 2)
        transaction.Put(takeFirst ? first : second, worker.NewValue(0));
      else if (firstOn == 0)
        transaction.Put(first, worker.NewValue(1));
      else
        transaction.Put(second, worker.NewValue(1));

      if (worker.Commit(transaction))
      {
        if (bothOff)
          worker.AddAnomalies();
        return;
      }
    }
  }

  long Read(IEngineTransaction transaction, long key)
  {
    var found = guarded
        ? transaction.TryGetForUpdate(key, out var value)
        : transaction.TryGet(key, out value);
    return found ? ValueFactory.Counter(value) : 0;
  }

  public override void Verify(ScenarioContext context)
  {
    var (_, count) = context.ScanSum(0, Groups * 2 - 1);
    context.Check(count == Groups * 2, $"expected {Groups * 2} keys, found {count}");
    if (!guarded)
      return;
    for (var group = 0L; group < Groups; group++)
    {
      var (sum, _) = context.ScanSum(group * 2, group * 2 + 1);
      context.Check(sum > 0, $"group {group} has nobody on call");
    }
  }
}

/// <summary>
/// The capacity rule: each partition holds at most a few rows. A transaction counts the
/// rows of a range and inserts one only below the limit. Under weak isolation, concurrent
/// inserts do not see each other: the phantom. The guarded variant serializes each
/// partition by writing a guard key, the usual fix without serializable ranges.
/// </summary>
public sealed class PhantomScenario(bool guarded)
    : Scenario(
        guarded ? "phantom-guarded" : "phantom",
        "isolation",
        guarded ? "Phantoms with a guard key" : "Phantoms with plain ranges",
        guarded
            ? "A rule spans a range of keys, and the application serializes it by updating a guard key."
            : "A rule spans a range of keys, and the application relies on the isolation level alone.",
        guarded
            ? "No range holds more rows than the limit."
            : "None: the anomaly count shows which engines allow phantoms.")
{
  const string RoleName = "transactions";
  const int Partitions = 16;
  const int RowLimit = 8;
  const int PartitionBits = 40;
  const long Job = 200_000;

  public override bool AnomaliesViolateInvariant => guarded;

  static long Guard(long partition) => partition << PartitionBits;

  static long LastRow(long partition) => Guard(partition + 1) - 1;

  static long NewRow(long partition, Worker worker) =>
      Guard(partition) + 1 + ((long)worker.Index << 28) + worker.NextSequence++;

  public override void Load(ScenarioContext context)
  {
    for (var partition = 0L; partition < Partitions; partition++)
      context.LoadCounters(Guard(partition), 1);
  }

  public override IReadOnlyList<Role> CreateRoles(ScenarioContext context) =>
      [new(RoleName, context.Workers, Run, context.Job(Job))];

  void Run(Worker worker)
  {
    var partition = (long)worker.Random.Next(Partitions);
    while (!worker.IsStopping)
    {
      using var transaction = worker.Session.BeginWrite();
      var version = 0L;
      if (guarded && transaction.TryGetForUpdate(Guard(partition), out var guardValue))
        version = ValueFactory.Counter(guardValue);

      var rows = 0;
      var firstRow = 0L;
      foreach (var record in transaction.Range(Guard(partition) + 1, LastRow(partition)))
      {
        if (rows++ == 0)
          firstRow = record.Key;
      }

      if (rows < RowLimit)
        transaction.Put(NewRow(partition, worker), worker.NewValue(1));
      else
        transaction.Delete(firstRow);
      if (guarded)
        transaction.Put(Guard(partition), worker.NewValue(version + 1));

      if (worker.Commit(transaction))
      {
        if (rows > RowLimit)
          worker.AddAnomalies();
        worker.AddRows(rows);
        return;
      }
    }
  }

  public override void Verify(ScenarioContext context)
  {
    for (var partition = 0L; partition < Partitions; partition++)
    {
      var (_, guards) = context.ScanSum(Guard(partition), Guard(partition));
      context.Check(guards == 1, $"partition {partition} lost its guard key");
      if (!guarded)
        continue;
      var (_, rows) = context.ScanSum(Guard(partition) + 1, LastRow(partition));
      context.Check(rows <= RowLimit, $"partition {partition} holds {rows} rows, above the limit of {RowLimit}");
    }
  }
}
