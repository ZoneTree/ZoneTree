using System.Diagnostics;
using Transactions.Benchmark.Engines;
using Transactions.Benchmark.Reporting;
using Transactions.Benchmark.Scenarios;

namespace Transactions.Benchmark.Measurement;

/// <summary>
/// Runs one engine in one scenario, in the process of the run:
/// load, settle, measure, close, reopen, verify, settle, measure storage.
/// </summary>
public static class ScenarioRunner
{
  public static RunResult Run(
      BenchmarkOptions options,
      Scenario scenario,
      EngineVariant variant,
      int workers,
      long keys)
  {
    var directory = Path.Combine(options.DataDirectory, $"{scenario.Id}-{variant.Id}-w{workers}-k{keys}");
    DeleteDirectory(directory);
    var engineOptions = new EngineOpenOptions(directory, scenario.Durability, options);

    using var monitor = new ProcessMonitor();
    var engine = variant.Open(engineOptions);
    var context = new ScenarioContext(options, workers, keys, engine)
    {
      AnalyzeInvariants = !variant.Raw
    };
    var settings = engine.Settings;

    var loadSeconds = Timed(() => scenario.Load(context));
    var settleAfterLoadSeconds = Timed(engine.Settle);

    monitor.ResetPeak();
    var measured = scenario.IsSingleShot
        ? RunSingleShot(scenario, context, monitor)
        : RunWorkers(scenario, context, monitor);
    var peakWorkingSet = monitor.PeakWorkingSetBytes;

    var closeSeconds = Timed(engine.Dispose);
    var reopenSeconds = Timed(() => engine = variant.Open(engineOptions));
    context.Engine = engine;

    if (context.AnalyzeInvariants)
    {
      scenario.Verify(context);
      if (scenario.AnomaliesViolateInvariant)
      {
        var anomalies = context.TotalAnomalies();
        context.Check(anomalies == 0, $"workers saw {anomalies} violations during the run");
      }
    }

    var finalSettleSeconds = Timed(engine.Settle);
    // Measured after the close: background compactions delete files while the engine is open.
    engine.Dispose();
    var storageBytes = DirectorySize(directory);
    if (!options.KeepData)
      DeleteDirectory(directory);

    var commits = measured.Roles.Sum(x => x.Commits);
    var operations = measured.Roles.Sum(x => x.Operations);
    var diskWrites = Difference(measured.Before.WriteBytes, measured.After.WriteBytes);
    var cpu = measured.After.CpuTime - measured.Before.CpuTime;
    return new RunResult
    {
      ScenarioId = scenario.Id,
      EngineId = variant.Id,
      Workers = workers,
      Keys = keys,
      Status = RunStatus.Ok,
      InvariantsChecked = context.AnalyzeInvariants,
      InvariantHolds = context.AnalyzeInvariants && context.FailedInvariantChecks.Count == 0,
      FailedChecks = context.FailedInvariantChecks,
      EngineSettings = settings,
      JobSeconds = measured.Seconds,
      Roles = measured.Roles,
      Phases = measured.Phases,
      LoadSeconds = loadSeconds,
      SettleAfterLoadSeconds = settleAfterLoadSeconds,
      CloseSeconds = closeSeconds,
      ReopenSeconds = reopenSeconds,
      FinalSettleSeconds = finalSettleSeconds,
      StorageBytes = storageBytes,
      PeakWorkingSetBytes = peakWorkingSet,
      CpuCores = cpu.TotalSeconds / Math.Max(measured.Seconds, 1e-9),
      CpuMicrosecondsPerOperation = operations > 0 ? cpu.TotalMicroseconds / operations : null,
      GcPauseMilliseconds = (measured.After.GcPause - measured.Before.GcPause).TotalMilliseconds,
      DiskWriteBytes = diskWrites,
      DiskWriteBytesPerCommit = diskWrites.HasValue && commits > 0 ? (double)diskWrites.Value / commits : null,
      DiskReadBytes = Difference(measured.Before.ReadBytes, measured.After.ReadBytes),
    };
  }

  static long? Difference(long? before, long? after) =>
      before.HasValue && after.HasValue ? after - before : null;

  sealed record Measured(
      double Seconds,
      ProcessSnapshot Before,
      ProcessSnapshot After,
      IReadOnlyList<RoleResult> Roles,
      IReadOnlyList<PhaseResult> Phases);

  static Measured RunSingleShot(Scenario scenario, ScenarioContext context, ProcessMonitor monitor)
  {
    var before = monitor.Snapshot();
    var start = Stopwatch.GetTimestamp();
    var phases = scenario.RunPhases(context);
    var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
    return new Measured(seconds, before, monitor.Snapshot(), [], phases);
  }

  /// <summary>
  /// Runs the job: the job's workers each do their share of its operations, and the run
  /// ends when the last one finishes. Background roles run until then.
  /// </summary>
  static Measured RunWorkers(Scenario scenario, ScenarioContext context, ProcessMonitor monitor)
  {
    const int seedStep = 1_000_003;
    var options = context.Options;
    var control = new RunControl();
    var roles = scenario.CreateRoles(context);
    var threadCount = roles.Sum(x => x.Workers);
    using var startBarrier = new Barrier(threadCount + 1);
    var jobThreads = new List<Thread>();
    var backgroundThreads = new List<Thread>();
    var workers = new List<(Role Role, Worker Worker)>();

    foreach (var role in roles)
    {
      for (var i = 0; i < role.Workers; i++)
      {
        var index = workers.Count;
        var worker = new Worker(
            control,
            context.Engine.OpenSession(),
            context.Values,
            options.RetryBackoff,
            index,
            options.Seed + index * seedStep,
            context.AnalyzeInvariants);
        context.AddWorker(role.Name, worker);
        workers.Add((role, worker));
        var share = role.Operations / role.Workers + (i < role.Operations % role.Workers ? 1 : 0);
        var thread = new Thread(() => RunWorker(role, worker, share, control, startBarrier))
        {
          IsBackground = true,
          Name = $"{role.Name}-{i}"
        };
        (role.IsJob ? jobThreads : backgroundThreads).Add(thread);
        thread.Start();
      }
    }

    monitor.ResetPeak();
    var before = monitor.Snapshot();
    var start = Stopwatch.GetTimestamp();
    startBarrier.SignalAndWait();
    foreach (var thread in jobThreads)
      thread.Join();
    var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
    var after = monitor.Snapshot();

    control.RequestStop();
    foreach (var thread in backgroundThreads)
      thread.Join();
    foreach (var (_, worker) in workers)
      worker.Session.Dispose();
    if (control.Failure != null)
      throw new InvalidOperationException($"A worker failed: {control.Failure.Message}", control.Failure);

    var results = roles
        .Select(role => Summarize(role, workers.Where(x => x.Role == role).Select(x => x.Worker).ToArray(), seconds))
        .ToArray();
    return new Measured(seconds, before, after, results, []);
  }

  static void RunWorker(Role role, Worker worker, long share, RunControl control, Barrier startBarrier)
  {
    startBarrier.SignalAndWait();
    try
    {
      for (var done = 0L; (!role.IsJob || done < share) && !control.IsStopping; done++)
      {
        var start = Stopwatch.GetTimestamp();
        role.Operation(worker);
        worker.CompleteOperation(Stopwatch.GetTimestamp() - start);
      }
    }
    catch (Exception exception)
    {
      control.ReportFailure(exception);
    }
  }

  static RoleResult Summarize(Role role, IReadOnlyList<Worker> workers, double seconds)
  {
    var latency = new LatencyHistogram();
    foreach (var worker in workers)
      latency.Merge(worker.Latency);
    var operations = workers.Sum(x => x.Operations);
    var commits = workers.Sum(x => x.Commits);
    var conflicts = workers.Sum(x => x.Conflicts);
    var rows = workers.Sum(x => x.Rows);
    return new RoleResult
    {
      Name = role.Name,
      Workers = role.Workers,
      IsPrimary = role.IsJob,
      Operations = operations,
      OperationsPerSecond = operations / seconds,
      Commits = commits,
      Conflicts = conflicts,
      ConflictPercent = commits + conflicts > 0 ? 100.0 * conflicts / (commits + conflicts) : 0,
      Rollbacks = workers.Sum(x => x.Rollbacks),
      Rows = rows,
      RowsPerSecond = rows / seconds,
      Anomalies = workers.Sum(x => x.Anomalies),
      Latency = latency.Summarize(),
    };
  }

  static double Timed(Action action)
  {
    var start = Stopwatch.GetTimestamp();
    action();
    return Stopwatch.GetElapsedTime(start).TotalSeconds;
  }

  static long DirectorySize(string directory) =>
      Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
          .Sum(file => new FileInfo(file).Length);

  public static void DeleteDirectory(string directory)
  {
    if (Directory.Exists(directory))
      Directory.Delete(directory, recursive: true);
  }
}
