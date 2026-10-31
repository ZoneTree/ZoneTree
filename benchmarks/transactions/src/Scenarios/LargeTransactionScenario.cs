using System.Diagnostics;
using Transactions.Benchmark.Measurement;
using Transactions.Benchmark.Reporting;

namespace Transactions.Benchmark.Scenarios;

/// <summary>
/// One big transaction, timed by phase: writing, committing, reading it back, and the
/// same size rolled back. Peak memory shows where the engine keeps uncommitted writes.
/// </summary>
public sealed class LargeTransactionScenario()
    : Scenario(
        "large-txn",
        "large",
        "One large transaction",
        "Migrations, imports and bulk corrections: what one huge transaction costs in time and memory, and what undoing it costs.",
        "The committed keys exist, and the rolled back keys do not.")
{
  public override bool IsSingleShot => true;

  public override bool ReportsRows => true;

  public override void Load(ScenarioContext context) =>
      context.LoadCounters(0, context.Keys);

  static long RowCount(ScenarioContext context) =>
      context.Options.LargeTransactionKeys > 0 ? context.Options.LargeTransactionKeys : context.Keys;

  public override IReadOnlyList<PhaseResult> RunPhases(ScenarioContext context)
  {
    var count = RowCount(context);
    var values = context.Values;
    var random = new Random(context.Options.Seed);
    var phases = new List<PhaseResult>();
    using var session = context.Engine.OpenSession();

    using (var transaction = session.BeginWrite())
    {
      phases.Add(Time("write", count, () =>
      {
        for (var i = 0L; i < count; i++)
          transaction.Put(InsertedKeys.Sequential(0, i), values.Create(i, random));
      }));
      phases.Add(Time("commit", count, () =>
      {
        if (!transaction.TryCommit())
          throw new InvalidOperationException("The large transaction conflicted, but it runs alone.");
      }));
    }

    using (var transaction = session.BeginRead())
    {
      phases.Add(Time("read back", count, () =>
      {
        var (_, found) = ScenarioContext.Scan(transaction, InsertedKeys.Base, long.MaxValue);
        if (found != count)
          throw new InvalidOperationException($"Read {found} of {count} committed keys.");
      }));
    }

    using (var transaction = session.BeginWrite())
    {
      phases.Add(Time("write to roll back", count, () =>
      {
        for (var i = 0L; i < count; i++)
          transaction.Put(InsertedKeys.Sequential(1, i), values.Create(i, random));
      }));
      phases.Add(Time("rollback", count, transaction.Rollback));
    }
    return phases;
  }

  static PhaseResult Time(string name, long rows, Action action)
  {
    var start = Stopwatch.GetTimestamp();
    action();
    return new PhaseResult(name, Stopwatch.GetElapsedTime(start).TotalSeconds, rows);
  }

  public override void Verify(ScenarioContext context)
  {
    var expected = RowCount(context);
    var (_, found) = context.ScanSum(InsertedKeys.Base, long.MaxValue);
    context.Check(found == expected, $"expected {expected} committed keys, found {found}");
  }
}
