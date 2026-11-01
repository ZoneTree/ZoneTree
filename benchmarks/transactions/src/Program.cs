using System.Diagnostics;
using System.Reflection;
using Transactions.Benchmark;
using Transactions.Benchmark.Engines;
using Transactions.Benchmark.Measurement;
using Transactions.Benchmark.Reporting;
using Transactions.Benchmark.Scenarios;

try
{
  var options = BenchmarkOptions.Parse(args);
  if (options.ShowHelp)
  {
    Console.WriteLine(BenchmarkOptions.HelpText);
    return 0;
  }
  if (options.ListScenarios)
  {
    PrintScenarios();
    return 0;
  }
  if (options.ChildRun)
    return RunChild(options);

  var root = FindProjectRoot();
  options = options with
  {
    DataDirectory = Path.GetFullPath(options.DataDirectory.Length > 0 ? options.DataDirectory : Path.Combine(root, "out", "data")),
    OutputDirectory = Path.GetFullPath(options.OutputDirectory.Length > 0 ? options.OutputDirectory : Path.Combine(root, "out", "results")),
  };

  var renderDirectory = options.RenderLatest ? ReferenceReport.Locate(root, options.Preset) : options.RenderReport;
  if (renderDirectory.Length > 0)
  {
    var directory = Path.GetFullPath(renderDirectory);
    IEnumerable<string> keyCounts = Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : [];
    var reports = keyCounts.Prepend(directory).Where(x => File.Exists(Path.Combine(x, "summary.json"))).ToArray();
    if (reports.Length == 0)
      throw new ArgumentException($"There is no report to render in {directory}.");
    foreach (var report in reports)
    {
      ReportWriter.Write(ResultFiles.Read<BenchmarkReport>(Path.Combine(report, "summary.json")), report);
      Console.WriteLine($"Rendered {Path.Combine(report, "summary.md")}");
    }
    return 0;
  }

  Clean(options);
  return await RunAllAsync(options, root);
}
catch (ArgumentException exception)
{
  await Console.Error.WriteLineAsync(exception.Message);
  return 2;
}

static void PrintScenarios()
{
  foreach (var category in ScenarioCatalog.Categories)
  {
    Console.WriteLine($"{category.Id}: {category.Question}");
    foreach (var scenario in ScenarioCatalog.All.Where(x => x.Category == category.Id))
      Console.WriteLine($"  {scenario.Id,-28} {scenario.Title}");
  }
}

/// <summary>
/// Runs one engine, scenario, worker count and key count, and reports through a file.
/// A failure becomes a result, so one broken run does not end the benchmark.
/// </summary>
static int RunChild(BenchmarkOptions options)
{
  var scenario = ScenarioCatalog.Find(options.Scenarios[0]);
  var engine = EngineCatalog.Resolve(options.Engines)[0];
  var workers = options.Workers[0];
  var keys = options.Keys[0];
  RunResult result;
  try
  {
    result = ScenarioRunner.Run(options, scenario, engine, workers, keys);
  }
  catch (Exception exception)
  {
    result = new RunResult
    {
      ScenarioId = scenario.Id,
      EngineId = engine.Id,
      Workers = workers,
      Keys = keys,
      Status = RunStatus.Failed,
      Error = exception.ToString(),
    };
  }
  ResultFiles.Write(options.ResultFile, result);
  return 0;
}

static async Task<int> RunAllAsync(BenchmarkOptions options, string root)
{
  var scenarios = ScenarioCatalog.Resolve(options.Scenarios);
  var engines = EngineCatalog.Resolve(options.Engines);
  var plan = (
      from keys in options.Keys
      from scenario in scenarios
      from workers in scenario.IsSingleShot ? [1] : options.Workers
      from engine in engines
      select (Scenario: scenario, Engine: engine, Workers: workers, Keys: keys)).ToArray();

  var settings = options.ComparisonSettings();
  if (options.UpdateLatest)
  {
    foreach (var keys in options.Keys)
      ReferenceReport.EnsureComparable(ReferenceReport.Locate(root, options.Preset, keys), settings, options.Force);
  }

  IReadOnlyList<BenchmarkReport> resumed = options.Resume ? ResumedExecution.FindLatest(options.OutputDirectory) : [];
  var stamp = resumed.Count > 0 ? resumed[0].Stamp : DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
  var output = Path.Combine(options.OutputDirectory, stamp);
  var runDirectory = Path.Combine(output, ".runs");
  Directory.CreateDirectory(runDirectory);
  Directory.CreateDirectory(options.DataDirectory);

  var environment = EnvironmentInfo.Describe();
  // A run that did not finish well runs again, since an interruption can be why it failed.
  var runs = resumed.SelectMany(x => x.Runs).Where(x => x.Status == RunStatus.Ok).ToList();
  BenchmarkReport CreateReport(long keys) => new()
  {
    Stamp = stamp,
    Preset = options.Preset,
    Keys = [keys],
    Workers = options.Workers,
    EngineIds = engines.Select(x => x.Id).ToArray(),
    ScenarioIds = scenarios.Select(x => x.Id).ToArray(),
    ValueSize = options.ValueSize,
    Scale = options.Scale,
    ZipfTheta = options.ZipfTheta,
    LargeTransactionKeys = options.LargeTransactionKeys,
    RetryBackoff = options.RetryBackoff,
    Seed = options.Seed,
    Environment = environment,
    Runs = runs.Where(x => x.Keys == keys).ToArray(),
    Settings = settings,
    PlannedRuns = plan.Count(x => x.Keys == keys),
  };

  foreach (var report in resumed)
    ResumedExecution.EnsureSameCommand(report, CreateReport(report.Keys[0]), options.Keys);
  var finished = runs.Select(ReferenceReport.Identify).ToHashSet();

  if (options.Resume && resumed.Count == 0)
    Console.WriteLine($"There is no execution to resume in {options.OutputDirectory}, so this one starts from the beginning.");
  Console.WriteLine(resumed.Count > 0
      ? $"Resuming {stamp}: {finished.Count} of {plan.Length} runs are done. Results: {output}"
      : $"Running {plan.Length} runs: {scenarios.Count} scenarios, {engines.Count} engines. Results: {output}");

  // Reports update between runs, so a stopped execution keeps what it measured.
  for (var i = 0; i < plan.Length; i++)
  {
    var (scenario, engine, workers, keys) = plan[i];
    if (finished.Contains((scenario.Id, engine.Id, workers, keys)))
      continue;
    var result = await RunInChildProcessAsync(options, runDirectory, scenario, engine, workers, keys);
    runs.Add(result);
    Console.WriteLine($"[{i + 1}/{plan.Length}] {scenario.Id} / {engine.Id} / {workers} workers / {Format.Compact(keys)} keys: {Describe(result)}");
    UpdateReport(ReportWriter.DirectoryOf(output, keys), () => CreateReport(keys));
    if (options.UpdateLatest)
    {
      var reference = ReferenceReport.Locate(root, options.Preset, keys);
      UpdateReport(reference, () => ReferenceReport.Merge(ReferenceReport.Read(reference), CreateReport(keys)));
    }
  }

  foreach (var keys in options.Keys)
  {
    Console.WriteLine($"Report: {Path.Combine(ReportWriter.DirectoryOf(output, keys), "summary.md")}");
    if (options.UpdateLatest)
      Console.WriteLine($"Reference: {Path.Combine(ReferenceReport.Locate(root, options.Preset, keys), "summary.md")}");
  }

  var unhealthy = runs.Count(x => x.Status != RunStatus.Ok || !x.InvariantHolds);
  if (unhealthy == 0)
    return 0;
  Console.WriteLine($"{unhealthy} of {runs.Count} runs failed, timed out or broke an invariant. See the report.");
  return 1;
}

static string Describe(RunResult result)
{
  if (result.Status != RunStatus.Ok)
    return result.Status;
  var primary = result.Primary;
  var rate = primary != null
      ? $"{Format.Seconds(result.JobSeconds)} for {Format.Compact(primary.Operations)} ops ({Format.Compact(primary.OperationsPerSecond)}/s), {Format.Percent(primary.ConflictPercent)} conflicts, {Format.Count(result.Roles.Sum(x => x.Anomalies))} anomalies"
      : Format.Seconds(result.JobSeconds);
  return result.InvariantHolds ? rate : $"{rate}, INVARIANT BROKEN: {string.Join("; ", result.FailedChecks)}";
}

static async Task<RunResult> RunInChildProcessAsync(
    BenchmarkOptions options,
    string runDirectory,
    Scenario scenario,
    EngineVariant engine,
    int workers,
    long keys)
{
  var assembly = Assembly.GetEntryAssembly()?.Location
      ?? throw new InvalidOperationException("Could not resolve the benchmark assembly path.");
  var resultFile = Path.Combine(runDirectory, $"{scenario.Id}-{engine.Id}-w{workers}-k{keys}.json");
  var start = new ProcessStartInfo("dotnet")
  {
    UseShellExecute = false,
    WorkingDirectory = Directory.GetCurrentDirectory(),
    RedirectStandardError = true
  };
  start.ArgumentList.Add(assembly);
  foreach (var argument in options.ToChildArguments(scenario.Id, engine.Id, workers, keys, resultFile))
    start.ArgumentList.Add(argument);

  using var process = Process.Start(start)
      ?? throw new InvalidOperationException($"Could not start the run of {engine.Id}.");
  var errors = process.StandardError.ReadToEndAsync();
  using var timeout = options.TimeoutSeconds > 0
      ? new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds))
      : new CancellationTokenSource();

  RunResult Failure(string status, string error) => new()
  {
    ScenarioId = scenario.Id,
    EngineId = engine.Id,
    Workers = workers,
    Keys = keys,
    Status = status,
    Error = error,
  };

  try
  {
    await process.WaitForExitAsync(timeout.Token);
  }
  catch (OperationCanceledException)
  {
    process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
    DeleteRunData(options, scenario, engine, workers, keys);
    return Failure(RunStatus.TimedOut, $"Killed after {options.TimeoutSeconds} s.");
  }

  if (process.ExitCode == 0 && File.Exists(resultFile))
    return ResultFiles.Read<RunResult>(resultFile);
  DeleteRunData(options, scenario, engine, workers, keys);
  return Failure(RunStatus.Failed, $"The process exited with code {process.ExitCode}. {await errors}");
}

static void DeleteRunData(BenchmarkOptions options, Scenario scenario, EngineVariant engine, int workers, long keys)
{
  try
  {
    ScenarioRunner.DeleteDirectory(Path.Combine(options.DataDirectory, $"{scenario.Id}-{engine.Id}-w{workers}-k{keys}"));
  }
  catch (IOException exception)
  {
    Console.WriteLine($"Could not delete the data of a failed run: {exception.Message}");
  }
}

static void Clean(BenchmarkOptions options)
{
  if ((options.Clean || options.CleanData) && Directory.Exists(options.DataDirectory))
    Directory.Delete(options.DataDirectory, recursive: true);
  if ((options.Clean || options.CleanResults) && Directory.Exists(options.OutputDirectory))
    Directory.Delete(options.OutputDirectory, recursive: true);
}

/// <summary>
/// A report that cannot be written, say because a viewer holds a file, must not end a long
/// execution. The next run's update repairs it.
/// </summary>
static void UpdateReport(string directory, Func<BenchmarkReport> report)
{
  try
  {
    ReportWriter.Write(report(), directory);
  }
  catch (Exception exception)
  {
    Console.WriteLine($"Could not update the report in {directory}: {exception.Message}");
  }
}

static string FindProjectRoot()
{
  var candidates = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
  foreach (var candidate in candidates)
  {
    for (var directory = new DirectoryInfo(candidate); directory != null; directory = directory.Parent)
    {
      if (File.Exists(Path.Combine(directory.FullName, "src", "Transactions.Benchmark.csproj")))
        return directory.FullName;
      var nested = Path.Combine(directory.FullName, "benchmarks", "transactions");
      if (File.Exists(Path.Combine(nested, "src", "Transactions.Benchmark.csproj")))
        return nested;
    }
  }
  throw new InvalidOperationException("Could not locate benchmarks/transactions. Run from inside the repository, or pass --data and --output.");
}
