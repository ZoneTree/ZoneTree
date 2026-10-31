using System.Globalization;
using System.Text;
using Transactions.Benchmark.Engines;
using Transactions.Benchmark.Scenarios;

namespace Transactions.Benchmark.Reporting;

/// <summary>
/// Writes a report folder: summary.md and summary.json, a page per scenario, and the charts.
/// Every link is relative, so the folder can be copied as a whole.
/// </summary>
public static class ReportWriter
{
  const string ChartsFolder = "charts";
  const string ScenariosFolder = "scenarios";

  /// <summary>
  /// Where the report of a key count goes. Each key count has its own, named like --keys.
  /// </summary>
  public static string DirectoryOf(string parent, long keys)
  {
    var name =
        keys % 1_000_000 == 0 ? (keys / 1_000_000).ToString(CultureInfo.InvariantCulture) + "M" :
        keys % 1_000 == 0 ? (keys / 1_000).ToString(CultureInfo.InvariantCulture) + "K" :
        keys.ToString(CultureInfo.InvariantCulture);
    return Path.Combine(parent, name);
  }

  public static void Write(BenchmarkReport report, string directory)
  {
    var charts = Path.Combine(directory, ChartsFolder);
    var pages = Path.Combine(directory, ScenariosFolder);
    Directory.CreateDirectory(charts);
    Directory.CreateDirectory(pages);

    // The report is rewritten over its previous version, whose charts may no longer apply.
    ResultFiles.Write(Path.Combine(directory, "summary.json"), report);
    foreach (var file in Directory.EnumerateFiles(charts).Concat(Directory.EnumerateFiles(pages)))
      File.Delete(file);

    File.WriteAllText(Path.Combine(directory, "summary.md"), RenderSummary(report, charts));
    foreach (var id in report.ScenarioIds.Where(id => report.Runs.Any(x => x.ScenarioId == id)))
    {
      var scenario = ScenarioCatalog.Find(id);
      File.WriteAllText(Path.Combine(pages, $"{id}.md"), RenderScenarioPage(report, scenario, charts));
    }
  }

  static string EngineName(string id) =>
      EngineCatalog.All.First(x => x.Id == id).DisplayName;

  static IReadOnlyList<string> Engines(BenchmarkReport report) =>
      report.EngineIds.OrderBy(EngineCatalog.ColorSlot).ToArray();

  static IReadOnlyList<string> EnginesOf(BenchmarkReport report, IReadOnlyList<Scenario> scenarios, long keys) =>
      Engines(report)
          .Where(engine => scenarios.Any(scenario => RunsOf(report, scenario.Id, keys).Any(x => x.EngineId == engine)))
          .ToArray();

  static IEnumerable<RunResult> RunsOf(BenchmarkReport report, string scenarioId, long keys) =>
      report.Runs.Where(x => x.ScenarioId == scenarioId && x.Keys == keys);

  /// <summary>
  /// The worker count a comparison reports: the one the most engines ran with, so a partial
  /// rerun at a new count does not hide the others. Among equals, the highest.
  /// </summary>
  static int ComparisonWorkers(IEnumerable<RunResult> runs) =>
      runs.GroupBy(x => x.Workers)
          .OrderByDescending(x => x.Select(run => run.EngineId).Distinct().Count())
          .ThenByDescending(x => x.Key)
          .Select(x => x.Key)
          .FirstOrDefault();

  static int ComparisonWorkers(BenchmarkReport report, string scenarioId, long keys) =>
      ComparisonWorkers(RunsOf(report, scenarioId, keys));

  /// <summary>
  /// The number a comparison uses for a run: rows per second where rows are the unit of work.
  /// </summary>
  static double Rate(Scenario scenario, RunResult run)
  {
    if (run.Status != RunStatus.Ok)
      return 0;
    if (scenario.IsSingleShot)
    {
      var phases = run.Phases.Where(x => x.Name is "write" or "commit").ToArray();
      var seconds = phases.Sum(x => x.Seconds);
      return seconds > 0 ? phases[0].Rows / seconds : 0;
    }
    var primary = run.Primary;
    if (primary == null)
      return 0;
    return scenario.ReportsRows ? primary.RowsPerSecond : primary.OperationsPerSecond;
  }

  static string RateUnit(Scenario scenario) =>
      scenario.ReportsRows ? "rows/s" : "ops/s";

  static string ProblemOf(RunResult run)
  {
    if (run.Status == RunStatus.TimedOut)
      return "timed out";
    if (run.Status != RunStatus.Ok)
      return "failed";
    return run.InvariantHolds ? "" : "invariant broken";
  }

  static string RenderSummary(BenchmarkReport report, string charts)
  {
    var md = new StringBuilder();
    md.AppendLine("# Transaction benchmark");
    md.AppendLine();
    var ok = report.Runs.Count(x => x.Status == RunStatus.Ok && x.InvariantHolds);
    var origin = report.Sources.Count > 1
        ? $"merged from {report.Sources.Count} executions, `{report.Sources[0].Stamp}` to `{report.Sources[^1].Stamp}`"
        : $"run `{report.Stamp}`";
    var unfinished = report.PlannedRuns > report.Runs.Count
        ? $" {report.PlannedRuns - report.Runs.Count} of the {report.PlannedRuns} planned runs have not finished."
        : "";
    md.AppendLine(string.Create(
        CultureInfo.InvariantCulture,
        $"Preset `{report.Preset}`, {origin}: {ok} of {report.Runs.Count} runs completed with their invariants holding.{unfinished}"));
    md.AppendLine();
    md.AppendLine(string.Create(
        CultureInfo.InvariantCulture,
        $"Each run does a fixed job, the same on every engine, and measures the time it takes: fresh database in its own process, job scale {report.Scale:0.###}, {report.ValueSize} byte values, zipf theta {report.ZipfTheta:0.##}{(report.RetryBackoff ? ", exponential backoff after conflicts" : ", immediate retry after conflicts")}."));
    md.AppendLine();
    WriteProblems(md, report);

    foreach (var keys in report.Keys.Where(keys => report.Runs.Any(x => x.Keys == keys)))
    {
      md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"## {Format.Compact(keys)} keys"));
      md.AppendLine();
      foreach (var category in ScenarioCatalog.Categories)
      {
        var scenarios = report.ScenarioIds
            .Select(ScenarioCatalog.Find)
            .Where(x => x.Category == category.Id && RunsOf(report, x.Id, keys).Any())
            .ToArray();
        if (scenarios.Length == 0)
          continue;
        md.AppendLine($"### {category.Title}");
        md.AppendLine();
        md.AppendLine(category.Question);
        md.AppendLine();
        WriteCategoryChart(md, report, category.Id, category.Title, scenarios, keys, charts);
        WriteCategoryTable(md, report, scenarios, keys);
        if (category.Id == "isolation")
          WriteAnomalyTable(md, report, scenarios, keys);
      }
    }

    WriteEngines(md, report);
    WriteEnvironment(md, report);
    return md.ToString();
  }

  /// <summary>
  /// What all runs share, then what only some executions had, such as another ZoneTree version.
  /// </summary>
  static void WriteEnvironment(StringBuilder md, BenchmarkReport report)
  {
    md.AppendLine("## Environment");
    md.AppendLine();
    IReadOnlyList<RunSource> sources = report.Sources.Count > 0 ? report.Sources : [new RunSource(report.Stamp, report.Environment)];
    var shared = sources
        .Select(x => (IEnumerable<string>)x.Environment)
        .Aggregate((a, b) => a.Intersect(b))
        .ToArray();
    foreach (var line in shared)
      md.AppendLine($"- {line}");
    foreach (var source in sources)
    {
      var own = source.Environment.Except(shared).ToArray();
      if (own.Length == 0)
        continue;
      md.AppendLine();
      md.AppendLine($"Only in `{source.Stamp}`:");
      md.AppendLine();
      foreach (var line in own)
        md.AppendLine($"- {line}");
    }
  }

  static void WriteProblems(StringBuilder md, BenchmarkReport report)
  {
    var problems = report.Runs.Where(x => ProblemOf(x).Length > 0).ToArray();
    if (problems.Length == 0)
      return;
    md.AppendLine("## Runs with problems");
    md.AppendLine();
    md.AppendLine("| Scenario | Engine | Workers | Keys | Problem |");
    md.AppendLine("| --- | --- | ---: | ---: | --- |");
    foreach (var run in problems)
    {
      var detail = run.Status == RunStatus.Ok
          ? string.Join("; ", run.FailedChecks)
          : FirstLine(run.Error);
      md.AppendLine(string.Create(
          CultureInfo.InvariantCulture,
          $"| [{run.ScenarioId}]({ScenariosFolder}/{run.ScenarioId}.md) | {EngineName(run.EngineId)} | {run.Workers} | {Format.Compact(run.Keys)} | {ProblemOf(run)}: {Cell(detail)} |"));
    }
    md.AppendLine();
  }

  static string FirstLine(string? text) =>
      string.IsNullOrEmpty(text) ? "" : text.Split('\n')[0].Trim();

  static string Cell(string text) =>
      text.Replace("|", "\\|", StringComparison.Ordinal);

  static void WriteCategoryChart(
      StringBuilder md,
      BenchmarkReport report,
      string categoryId,
      string categoryTitle,
      IReadOnlyList<Scenario> scenarios,
      long keys,
      string charts)
  {
    var groups = new List<(string, IReadOnlyList<(string, double, string)>)>();
    foreach (var scenario in scenarios)
    {
      var workers = ComparisonWorkers(report, scenario.Id, keys);
      var bars = RunsOf(report, scenario.Id, keys)
          .Where(x => x.Workers == workers && Rate(scenario, x) > 0)
          .OrderBy(x => EngineCatalog.ColorSlot(x.EngineId))
          .Select(x => (x.EngineId, Rate(scenario, x), $"{Format.Compact(Rate(scenario, x))} {RateUnit(scenario)}"))
          .ToArray();
      groups.Add((scenario.Id, bars));
    }
    if (groups.All(x => x.Item2.Count == 0))
      return;

    var file = string.Create(CultureInfo.InvariantCulture, $"category-{categoryId}-k{keys}.svg");
    File.WriteAllText(
        Path.Combine(charts, file),
        SvgCharts.RelativeBars(
            categoryTitle,
            "Speed finishing each job at the worker count in the table below. The fastest engine is 100%.",
            groups));
    md.AppendLine($"![{categoryTitle}]({ChartsFolder}/{file})");
    md.AppendLine();
  }

  static void WriteCategoryTable(StringBuilder md, BenchmarkReport report, IReadOnlyList<Scenario> scenarios, long keys)
  {
    var engines = EnginesOf(report, scenarios, keys);
    md.AppendLine("Time to finish the job, then its rate. The fastest engine is in bold.");
    md.AppendLine();
    md.AppendLine($"| Scenario | Job | Workers | {string.Join(" | ", engines.Select(EngineName))} |");
    md.AppendLine($"| --- | ---: | ---: |{string.Concat(engines.Select(_ => " ---: |"))}");
    foreach (var scenario in scenarios)
    {
      var workers = ComparisonWorkers(report, scenario.Id, keys);
      var runs = RunsOf(report, scenario.Id, keys).Where(x => x.Workers == workers).ToArray();
      var best = runs.Length == 0 ? 0 : runs.Max(x => Rate(scenario, x));
      var cells = engines.Select(engine =>
      {
        var run = runs.FirstOrDefault(x => x.EngineId == engine);
        if (run == null)
          return "";
        var problem = ProblemOf(run);
        if (run.Status != RunStatus.Ok)
          return problem;
        var rate = Rate(scenario, run);
        var time = Format.Seconds(JobTime(scenario, run));
        var text = rate == best ? $"**{time}**" : time;
        text += $" ({Format.Compact(rate)} {RateUnit(scenario)})";
        return problem.Length > 0 ? $"{text} ({problem})" : text;
      });
      md.AppendLine(string.Create(
          CultureInfo.InvariantCulture,
          $"| [{scenario.Id}]({ScenariosFolder}/{scenario.Id}.md) | {JobSize(scenario, runs)} | {workers} | {string.Join(" | ", cells)} |"));
    }
    md.AppendLine();
  }

  /// <summary>
  /// The time the comparison reports. A large transaction's job is its write and commit.
  /// </summary>
  static double JobTime(Scenario scenario, RunResult run) =>
      scenario.IsSingleShot
          ? run.Phases.Where(x => x.Name is "write" or "commit").Sum(x => x.Seconds)
          : run.JobSeconds;

  static string JobSize(Scenario scenario, IReadOnlyList<RunResult> runs)
  {
    var run = runs.FirstOrDefault(x => x.Status == RunStatus.Ok);
    if (run == null)
      return "";
    if (scenario.IsSingleShot)
      return $"{Format.Compact(run.Phases[0].Rows)} rows";
    var primary = run.Primary;
    return primary == null ? "" : $"{Format.Compact(primary.Operations)} ops";
  }

  static void WriteAnomalyTable(StringBuilder md, BenchmarkReport report, IReadOnlyList<Scenario> scenarios, long keys)
  {
    var engines = EnginesOf(report, scenarios, keys);
    md.AppendLine("Transactions that committed after observing a broken rule. Zero means the engine kept the rule in this run; a run cannot prove it always will.");
    md.AppendLine();
    md.AppendLine($"| Scenario | Workers | {string.Join(" | ", engines.Select(EngineName))} |");
    md.AppendLine($"| --- | ---: |{string.Concat(engines.Select(_ => " ---: |"))}");
    foreach (var scenario in scenarios)
    {
      var workers = ComparisonWorkers(report, scenario.Id, keys);
      var runs = RunsOf(report, scenario.Id, keys).Where(x => x.Workers == workers).ToArray();
      var cells = engines.Select(engine =>
      {
        var run = runs.FirstOrDefault(x => x.EngineId == engine);
        if (run == null)
          return "";
        return run.Status == RunStatus.Ok ? Format.Count(run.Roles.Sum(x => x.Anomalies)) : ProblemOf(run);
      });
      md.AppendLine(string.Create(
          CultureInfo.InvariantCulture,
          $"| [{scenario.Id}]({ScenariosFolder}/{scenario.Id}.md) | {workers} | {string.Join(" | ", cells)} |"));
    }
    md.AppendLine();
  }

  static void WriteEngines(StringBuilder md, BenchmarkReport report)
  {
    md.AppendLine("## Engines");
    md.AppendLine();
    foreach (var id in Engines(report))
    {
      var engine = EngineCatalog.All.First(x => x.Id == id);
      md.AppendLine($"### {engine.DisplayName}");
      md.AppendLine();
      md.AppendLine(engine.Isolation);
      md.AppendLine();
      var settings = report.Runs
          .Where(x => x.EngineId == id && x.EngineSettings.Count > 0)
          .GroupBy(x => string.Join("\n", x.EngineSettings))
          .OrderByDescending(x => x.Count())
          .FirstOrDefault();
      if (settings == null)
        continue;
      foreach (var setting in settings.First().EngineSettings)
        md.AppendLine($"- {setting}");
      md.AppendLine();
    }
    md.AppendLine("### Durability");
    md.AppendLine();
    md.AppendLine("The `durability` scenarios make every commit durable before it returns, on every engine: ZoneTree with `TryCommitDurableAsync`, RocksDB with `sync=true`, SQLite with `synchronous=FULL`.");
    md.AppendLine();
    md.AppendLine("The other scenarios use each engine's usual setting, where a commit does not wait for the disk:");
    md.AppendLine();
    md.AppendLine("| Engine | Setting | Process crash can lose | Power loss can lose |");
    md.AppendLine("| --- | --- | --- | --- |");
    md.AppendLine("| ZoneTree sync | `SyncCompressed` WAL | Nothing. | What the OS has not written back since the last tail sync. The tail is synced every `TailWriterJobInterval`, and each block when it reaches `CompressionBlockSize`, so at most one interval of commits, within one block. |");
    md.AppendLine("| ZoneTree async | `AsyncCompressed` WAL | Commits the background writer has not written to a file yet: about one `EmptyQueuePollInterval` under light load, one `CompressionBlockSize` block plus its queue under sustained load. | Commits the background writer has not synced to the disk yet: about one `EmptyQueuePollInterval` under light load, one `CompressionBlockSize` block plus its queue under sustained load. It syncs each full block and whenever its queue empties. |");
    md.AppendLine("| RocksDB | `sync=false` | Nothing. | What the OS has not written back since the last memtable flush. The WAL is not synced per commit or on a timer, and a flush makes its data durable, so at most the commits in the memtables: up to `max_write_buffer_number` buffers of `write_buffer_size`. |");
    md.AppendLine("| SQLite | `synchronous=NORMAL` | Nothing. | What the OS has not written back since the last checkpoint. A checkpoint syncs, and runs about every `wal_autocheckpoint` WAL pages, so at most about that much WAL, or more while readers hold a checkpoint back. |");
    md.AppendLine();
    md.AppendLine("`SyncCompressed`, RocksDB and SQLite all lose nothing on a process crash, and on a power loss all three lose what the OS has not written back, so a real loss is usually smaller than these limits. `AsyncCompressed` commits without waiting for the writer, which is what makes it the faster mode, so its window covers a process crash too. On every engine and mode, what survives is consistent, and an application that needs a commit to survive anything waits for it, as the `durability` category measures.");
    md.AppendLine();
  }

  static string RenderScenarioPage(BenchmarkReport report, Scenario scenario, string charts)
  {
    var md = new StringBuilder();
    md.AppendLine($"# {scenario.Title}");
    md.AppendLine();
    md.AppendLine($"[Back to the summary](../summary.md) · `{scenario.Id}` · category `{scenario.Category}`");
    md.AppendLine();
    md.AppendLine($"**Use case.** {scenario.UseCase}");
    md.AppendLine();
    md.AppendLine($"**Checked.** {scenario.Invariant}");
    md.AppendLine();

    foreach (var keys in report.Keys)
    {
      var runs = RunsOf(report, scenario.Id, keys)
          .OrderBy(x => EngineCatalog.ColorSlot(x.EngineId))
          .ThenBy(x => x.Workers)
          .ToArray();
      if (runs.Length == 0)
        continue;
      md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"## {Format.Compact(keys)} keys"));
      md.AppendLine();
      WriteMeasured(md, runs);
      if (scenario.IsSingleShot)
        WritePhases(md, scenario, runs, keys, charts);
      else
        WriteWorkerRuns(md, scenario, runs, keys, charts);
      WriteCosts(md, runs);
      WriteFailures(md, runs);
    }
    return md.ToString();
  }

  /// <summary>
  /// Says which execution measured what, when a merged report's runs differ in that.
  /// </summary>
  static void WriteMeasured(StringBuilder md, IReadOnlyList<RunResult> runs)
  {
    var executions = runs.GroupBy(x => x.Stamp).OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
    if (executions.Length < 2)
      return;
    md.AppendLine("These runs come from different executions:");
    md.AppendLine();
    foreach (var execution in executions)
    {
      var measured = execution
          .GroupBy(x => x.EngineId)
          .Select(x => $"{EngineName(x.Key)} ({string.Join(", ", x.Select(run => run.Workers))} workers)");
      md.AppendLine($"- `{execution.Key}`: {string.Join(", ", measured)}");
    }
    md.AppendLine();
  }

  static void WriteWorkerRuns(
      StringBuilder md,
      Scenario scenario,
      IReadOnlyList<RunResult> runs,
      long keys,
      string charts)
  {
    var ok = runs.Where(x => x.Status == RunStatus.Ok && x.Primary != null).ToArray();
    var workerLabels = runs.Select(x => x.Workers).Distinct().Order()
        .Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray();
    var prefix = string.Create(CultureInfo.InvariantCulture, $"{scenario.Id}-k{keys}");

    if (ok.Length > 0)
    {
      var throughput = ok.GroupBy(x => x.EngineId)
          .Select(x => new ChartSeries(
              x.Key,
              x.ToDictionary(r => r.Workers.ToString(CultureInfo.InvariantCulture), r => Rate(scenario, r))))
          .ToArray();
      WriteChart(md, charts, $"{prefix}-throughput.svg", "Throughput", SvgCharts.Lines(
          $"{scenario.Title}: throughput",
          $"{(scenario.ReportsRows ? "Rows" : "Operations")} per second by worker threads. Higher is better.",
          "worker threads",
          workerLabels,
          throughput,
          Format.Compact,
          allowLogAxis: true));

      var comparison = ComparisonWorkers(ok);
      var latency = ok.Where(x => x.Workers == comparison && x.Primary!.Operations > 0)
          .Select(x => (x.EngineId, x.Primary!.Latency.P50Us, x.Primary.Latency.P95Us, x.Primary.Latency.P99Us, x.Primary.Latency.P999Us))
          .ToArray();
      if (latency.Length > 0)
      {
        WriteChart(md, charts, $"{prefix}-latency.svg", "Latency", SvgCharts.LatencyRanges(
            $"{scenario.Title}: latency",
            string.Create(CultureInfo.InvariantCulture, $"{comparison} workers, including retries. Markers: p50, p95, p99, p99.9. Lower is better."),
            latency));
      }

      if (ok.Any(x => x.Primary!.Conflicts > 0))
      {
        var conflicts = ok.GroupBy(x => x.EngineId)
            .Select(x => new ChartSeries(
                x.Key,
                x.ToDictionary(r => r.Workers.ToString(CultureInfo.InvariantCulture), r => r.Primary!.ConflictPercent)))
            .ToArray();
        WriteChart(md, charts, $"{prefix}-conflicts.svg", "Conflicts", SvgCharts.Lines(
            $"{scenario.Title}: conflicts",
            "Share of commit attempts that conflicted and had to retry. Lower is better.",
            "worker threads",
            workerLabels,
            conflicts,
            Format.Percent,
            allowLogAxis: false));
      }
    }

    md.AppendLine("### Results");
    md.AppendLine();
    md.AppendLine("| Engine | Workers | Job | Time | Ops/s | Rows/s | Conflicts | p50 | p95 | p99 | p99.9 | Max | Anomalies | Invariant |");
    md.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
    foreach (var run in runs)
    {
      var primary = run.Primary;
      if (run.Status != RunStatus.Ok || primary == null)
      {
        md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {EngineName(run.EngineId)} | {run.Workers} | | | | | | | | | | | | {ProblemOf(run)} |"));
        continue;
      }
      md.AppendLine(string.Create(
          CultureInfo.InvariantCulture,
          $"| {EngineName(run.EngineId)} | {run.Workers} | {Format.Compact(primary.Operations)} | {Format.Seconds(run.JobSeconds)} | {Format.Compact(primary.OperationsPerSecond)} | {(primary.Rows > 0 ? Format.Compact(primary.RowsPerSecond) : "")} | {Format.Percent(primary.ConflictPercent)} | {Format.Microseconds(primary.Latency.P50Us)} | {Format.Microseconds(primary.Latency.P95Us)} | {Format.Microseconds(primary.Latency.P99Us)} | {Format.Microseconds(primary.Latency.P999Us)} | {Format.Microseconds(primary.Latency.MaxUs)} | {Format.Count(run.Roles.Sum(x => x.Anomalies))} | {(run.InvariantHolds ? "holds" : "BROKEN")} |"));
    }
    md.AppendLine();

    var background = runs.Where(x => x.Status == RunStatus.Ok)
        .SelectMany(run => run.Roles.Where(role => !role.IsPrimary && role.Operations > 0).Select(role => (run, role)))
        .ToArray();
    if (background.Length == 0)
      return;
    md.AppendLine("### Background roles");
    md.AppendLine();
    md.AppendLine("| Engine | Workers | Role | Role threads | Ops/s | Rows/s | Conflicts | p50 | p99 |");
    md.AppendLine("| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
    foreach (var (run, role) in background)
    {
      md.AppendLine(string.Create(
          CultureInfo.InvariantCulture,
          $"| {EngineName(run.EngineId)} | {run.Workers} | {role.Name} | {role.Workers} | {Format.Compact(role.OperationsPerSecond)} | {(role.Rows > 0 ? Format.Compact(role.RowsPerSecond) : "")} | {Format.Percent(role.ConflictPercent)} | {Format.Microseconds(role.Latency.P50Us)} | {Format.Microseconds(role.Latency.P99Us)} |"));
    }
    md.AppendLine();
  }

  static void WritePhases(StringBuilder md, Scenario scenario, IReadOnlyList<RunResult> runs, long keys, string charts)
  {
    var ok = runs.Where(x => x.Status == RunStatus.Ok).ToArray();
    if (ok.Length == 0)
      return;
    var phaseNames = ok[0].Phases.Select(x => x.Name).ToArray();
    var groups = phaseNames
        .Select(phase => (
            phase,
            (IReadOnlyList<(string, double, string)>)ok
                .Select(run => (run, seconds: run.Phases.First(x => x.Name == phase).Seconds))
                .Select(x => (x.run.EngineId, 1 / Math.Max(x.seconds, 1e-9), Format.Seconds(x.seconds)))
                .ToArray()))
        .ToArray();
    WriteChart(md, charts, string.Create(CultureInfo.InvariantCulture, $"{scenario.Id}-k{keys}-phases.svg"), "Phases", SvgCharts.RelativeBars(
        $"{scenario.Title}: speed by phase",
        "Each phase's fastest engine is 100%.",
        groups));

    md.AppendLine("### Phases");
    md.AppendLine();
    md.AppendLine($"| Engine | {string.Join(" | ", phaseNames)} | Invariant |");
    md.AppendLine($"| --- |{string.Concat(phaseNames.Select(_ => " ---: |"))} --- |");
    foreach (var run in runs)
    {
      var cells = phaseNames.Select(name =>
      {
        var phase = run.Phases.FirstOrDefault(x => x.Name == name);
        return phase == null ? "" : Format.Seconds(phase.Seconds);
      });
      md.AppendLine($"| {EngineName(run.EngineId)} | {string.Join(" | ", cells)} | {(run.Status == RunStatus.Ok && run.InvariantHolds ? "holds" : ProblemOf(run))} |");
    }
    md.AppendLine();
  }

  static void WriteChart(StringBuilder md, string charts, string file, string alt, string svg)
  {
    File.WriteAllText(Path.Combine(charts, file), svg);
    md.AppendLine($"![{alt}](../{ChartsFolder}/{file})");
    md.AppendLine();
  }

  static void WriteCosts(StringBuilder md, IReadOnlyList<RunResult> runs)
  {
    var ok = runs.Where(x => x.Status == RunStatus.Ok).ToArray();
    if (ok.Length == 0)
      return;
    md.AppendLine("### Costs");
    md.AppendLine();
    md.AppendLine("CPU, memory and disk writes cover the job. Reopen follows a clean close without settling first. Storage is measured after a final settle and close.");
    md.AppendLine();
    md.AppendLine("| Engine | Workers | CPU cores | CPU/op | Disk writes | Disk writes/commit | Peak memory | GC pause | Load | Close | Reopen | Settle | Storage |");
    md.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
    foreach (var run in ok)
    {
      md.AppendLine(string.Create(
          CultureInfo.InvariantCulture,
          $"| {EngineName(run.EngineId)} | {run.Workers} | {run.CpuCores:0.0} | {(run.CpuMicrosecondsPerOperation.HasValue ? Format.Microseconds(run.CpuMicrosecondsPerOperation.Value) : "")} | {Format.OptionalBytes(run.DiskWriteBytes)} | {Format.OptionalBytes(run.DiskWriteBytesPerCommit)} | {Format.Bytes(run.PeakWorkingSetBytes)} | {run.GcPauseMilliseconds:0} ms | {Format.Seconds(run.LoadSeconds)} | {Format.Seconds(run.CloseSeconds)} | {Format.Seconds(run.ReopenSeconds)} | {Format.Seconds(run.FinalSettleSeconds)} | {Format.Bytes(run.StorageBytes)} |"));
    }
    md.AppendLine();
  }

  static void WriteFailures(StringBuilder md, IReadOnlyList<RunResult> runs)
  {
    var failures = runs.Where(x => ProblemOf(x).Length > 0).ToArray();
    if (failures.Length == 0)
      return;
    md.AppendLine("### Problems");
    md.AppendLine();
    foreach (var run in failures)
    {
      md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"**{EngineName(run.EngineId)}, {run.Workers} workers: {ProblemOf(run)}**"));
      md.AppendLine();
      md.AppendLine("```text");
      md.AppendLine(run.Status == RunStatus.Ok ? string.Join(Environment.NewLine, run.FailedChecks) : run.Error);
      md.AppendLine("```");
      md.AppendLine();
    }
  }
}
