using System.Runtime.InteropServices;
using Transactions.Benchmark.Engines;
using Transactions.Benchmark.Scenarios;

namespace Transactions.Benchmark.Reporting;

/// <summary>
/// A report under reference/: the latest result of every run at one key count, across benchmark
/// executions. A run replaces the one with the same scenario, engine and worker count.
/// </summary>
public static class ReferenceReport
{
  public static string Locate(string root, string preset)
  {
    var platform =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" : "unknown";
    return Path.Combine(root, "reference", platform, preset);
  }

  public static string Locate(string root, string preset, long keys) =>
      ReportWriter.DirectoryOf(Locate(root, preset), keys);

  public static BenchmarkReport? Read(string directory)
  {
    var file = Path.Combine(directory, "summary.json");
    return File.Exists(file) ? ResultFiles.Read<BenchmarkReport>(file) : null;
  }

  /// <summary>
  /// Refuses a run before it starts when its results would not compare with the reference,
  /// unless forced. A report without settings predates them, and its workload is taken on trust.
  /// </summary>
  public static void EnsureComparable(string directory, IReadOnlyList<string> settings, bool force)
  {
    var reference = Read(directory);
    if (reference == null || reference.Settings.Count == 0 || reference.Settings.SequenceEqual(settings))
      return;
    var difference =
        $"""
        --update-latest would mix different settings in {directory}.
          Reference only: {string.Join("; ", reference.Settings.Except(settings))}
          This run only:  {string.Join("; ", settings.Except(reference.Settings))}
        """;
    if (force)
    {
      Console.WriteLine($"{difference}{Environment.NewLine}Mixing them because of --force. The report will warn about it.");
      return;
    }
    throw new ArgumentException(
        $"{difference}{Environment.NewLine}Use the reference's settings, delete that folder to start a new reference, or pass --force to mix them.");
  }

  /// <summary>
  /// Merges an execution's runs so far. Merging all of them each time means a failed update
  /// is repaired by the next one. Runs of a scenario or engine that left the catalog are dropped.
  /// </summary>
  public static BenchmarkReport Merge(BenchmarkReport? reference, BenchmarkReport report)
  {
    var latest = report.Runs.Select(x => x with { Stamp = report.Stamp }).ToArray();
    var replaced = latest.Select(Identify).ToHashSet();
    IEnumerable<RunResult> previous = reference == null
        ? []
        : reference.Runs
            .Select(x => x with { Stamp = x.Stamp ?? reference.Stamp })
            .Where(x => !replaced.Contains(Identify(x)));

    var scenarios = ScenarioCatalog.All.Select(x => x.Id).ToList();
    var runs = previous.Concat(latest)
        .Where(x => scenarios.Contains(x.ScenarioId) && EngineCatalog.All.Any(e => e.Id == x.EngineId))
        .OrderBy(x => scenarios.IndexOf(x.ScenarioId))
        .ThenBy(x => x.Workers)
        .ThenBy(x => EngineCatalog.ColorSlot(x.EngineId))
        .ToArray();

    IReadOnlyList<RunSource> known =
        reference == null ? [] :
        reference.Sources.Count > 0 ? reference.Sources :
        [new RunSource(reference.Stamp, reference.Environment)];
    // Executions recorded without settings all ran under the reference's, which were enforced.
    var sources = known
        .Where(x => x.Stamp != report.Stamp)
        .Select(x => x.Settings.Count > 0 ? x : x with { Settings = reference!.Settings })
        .Append(new RunSource(report.Stamp, report.Environment) { Settings = report.Settings })
        .Where(x => runs.Any(run => run.Stamp == x.Stamp))
        .OrderBy(x => x.Stamp, StringComparer.Ordinal)
        .ToArray();

    return report with
    {
      Keys = runs.Select(x => x.Keys).Distinct().ToArray(),
      Workers = runs.Select(x => x.Workers).Distinct().Order().ToArray(),
      EngineIds = runs.Select(x => x.EngineId).Distinct().OrderBy(EngineCatalog.ColorSlot).ToArray(),
      ScenarioIds = runs.Select(x => x.ScenarioId).Distinct().ToArray(),
      PlannedRuns = 0,
      Sources = sources,
      Runs = runs,
    };
  }

  public static (string, string, int, long) Identify(RunResult run) =>
      (run.ScenarioId, run.EngineId, run.Workers, run.Keys);
}
