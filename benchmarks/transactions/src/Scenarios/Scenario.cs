using Transactions.Benchmark.Engines;
using Transactions.Benchmark.Measurement;
using Transactions.Benchmark.Reporting;

namespace Transactions.Benchmark.Scenarios;

/// <summary>
/// One kind of worker in a run.
/// </summary>
/// <param name="Name">The role name in the report.</param>
/// <param name="Workers">The worker threads of the role.</param>
/// <param name="Operation">One unit of work, including its retries.</param>
/// <param name="Operations">
/// The job: the operations the workers share, after which the run ends. The charts and
/// comparison tables report this role. Zero for a background role, which repeats its
/// operation until the job is done.
/// </param>
public sealed record Role(string Name, int Workers, Action<Worker> Operation, long Operations = 0)
{
  public bool IsJob => Operations > 0;
}

/// <summary>
/// What a scenario needs to know about the run, and what it reads back after it.
/// </summary>
public sealed class ScenarioContext(BenchmarkOptions options, int workers, long keys, ITransactionEngine engine)
{
  const double JobReferenceKeys = 100_000;

  readonly List<string> FailedChecks = [];
  readonly Dictionary<string, List<Worker>> RoleWorkers = [];

  public BenchmarkOptions Options { get; } = options;

  public int Workers { get; } = workers;

  public long Keys { get; } = keys;

  public bool AnalyzeInvariants { get; init; } = true;

  /// <summary>
  /// The opened engine. The runner replaces it when it reopens the database.
  /// </summary>
  public ITransactionEngine Engine { get; set; } = engine;

  public ValueFactory Values { get; } = new(options.ValueSize, options.Seed);

  public IReadOnlyList<string> FailedInvariantChecks => FailedChecks;

  public void AddWorker(string role, Worker worker)
  {
    if (!RoleWorkers.TryGetValue(role, out var list))
      RoleWorkers[role] = list = [];
    list.Add(worker);
  }

  /// <summary>
  /// The size of a job. The argument is its size at 100K keys and scale 1, and it grows
  /// with both, so the key count sets how long a run takes. Every worker gets at least
  /// one operation.
  /// </summary>
  public long Job(double operations) =>
      FixedJob(operations * Keys / JobReferenceKeys);

  /// <summary>
  /// The size of a job whose operations already grow with the key count, like full scans.
  /// </summary>
  public long FixedJob(double operations) =>
      Math.Max(Workers, (long)Math.Ceiling(operations * Options.Scale));

  public long TotalCommits(string role) =>
      RoleWorkers.TryGetValue(role, out var list) ? list.Sum(x => x.Commits) : 0;

  public long TotalAnomalies() =>
      RoleWorkers.Values.SelectMany(x => x).Sum(x => x.Anomalies);

  public void Check(bool condition, string failure)
  {
    if (AnalyzeInvariants && !condition)
      FailedChecks.Add(failure);
  }

  /// <summary>
  /// Adds up the counters of the records between two keys in one snapshot.
  /// </summary>
  public (long Sum, long Count) ScanSum(long from, long to)
  {
    using var session = Engine.OpenSession();
    using var transaction = session.BeginRead();
    return Scan(transaction, from, to);
  }

  public static (long Sum, long Count) Scan(IEngineReadTransaction transaction, long from, long to, bool sumValues = true)
  {
    var sum = 0L;
    var count = 0L;
    foreach (var record in transaction.Range(from, to))
    {
      if (sumValues)
        sum += ValueFactory.Counter(record.Value);
      ++count;
    }
    return (sum, count);
  }

  /// <summary>
  /// Writes consecutive keys with the same counter, in batches.
  /// </summary>
  public void LoadCounters(long firstKey, long count, long counter = 0)
  {
    const int batchSize = 1_000;
    var random = new Random(Options.Seed);
    using var session = Engine.OpenSession();
    for (var loaded = 0L; loaded < count;)
    {
      using var transaction = session.BeginWrite();
      var batch = Math.Min(batchSize, count - loaded);
      for (var i = 0L; i < batch; i++)
        transaction.Put(firstKey + loaded + i, Values.Create(counter, random));
      if (!transaction.TryCommit())
        throw new InvalidOperationException("A load transaction conflicted, but the load runs alone.");
      loaded += batch;
    }
  }
}

public abstract class Scenario(
    string id,
    string category,
    string title,
    string useCase,
    string invariant)
{
  public string Id { get; } = id;

  public string Category { get; } = category;

  public string Title { get; } = title;

  /// <summary>
  /// The application question this scenario answers.
  /// </summary>
  public string UseCase { get; } = useCase;

  /// <summary>
  /// What the run checks after reopening the database.
  /// </summary>
  public string Invariant { get; } = invariant;

  public virtual DurabilityMode Durability => DurabilityMode.Relaxed;

  /// <summary>
  /// False when a weak isolation level is expected to let anomalies through:
  /// the report then shows the count as a property of the engine, not as a failure.
  /// </summary>
  public virtual bool AnomaliesViolateInvariant => true;

  /// <summary>
  /// True when rows per second say more than operations per second.
  /// </summary>
  public virtual bool ReportsRows => false;

  /// <summary>
  /// True when the scenario times its own phases once instead of running workers.
  /// </summary>
  public virtual bool IsSingleShot => false;

  public abstract void Load(ScenarioContext context);

  public virtual IReadOnlyList<Role> CreateRoles(ScenarioContext context) => [];

  public virtual IReadOnlyList<PhaseResult> RunPhases(ScenarioContext context) => [];

  public abstract void Verify(ScenarioContext context);
}
