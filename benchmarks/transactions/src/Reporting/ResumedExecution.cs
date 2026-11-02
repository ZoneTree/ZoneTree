namespace Transactions.Benchmark.Reporting;

/// <summary>
/// The execution --resume continues: the newest one that finished a run. Its reports are
/// written after every run, so they already have what resuming needs.
/// </summary>
public static class ResumedExecution
{
  /// <summary>
  /// The report of each key count of that execution, or none if there is no such execution.
  /// </summary>
  public static IReadOnlyList<BenchmarkReport> FindLatest(string outputDirectory)
  {
    if (!Directory.Exists(outputDirectory))
      return [];
    foreach (var execution in Directory.EnumerateDirectories(outputDirectory).OrderDescending(StringComparer.Ordinal))
    {
      var reports = Directory.EnumerateDirectories(execution)
          .Select(ReferenceReport.Read)
          .OfType<BenchmarkReport>()
          .ToArray();
      if (reports.Length > 0)
        return reports;
    }
    return [];
  }

  /// <summary>
  /// Refuses to continue an execution under another command or environment, because
  /// its report describes all of its runs as one execution.
  /// </summary>
  public static void EnsureSameCommand(BenchmarkReport previous, BenchmarkReport current, IReadOnlyList<long> keys)
  {
    IEnumerable<(string Name, IEnumerable<string> Previous, IEnumerable<string> Current)> compared =
    [
      ("Preset", [previous.Preset], [current.Preset]),
      ("Workers", previous.Workers.Select(Text), current.Workers.Select(Text)),
      ("Engines", previous.EngineIds, current.EngineIds),
      ("Scenarios", previous.ScenarioIds, current.ScenarioIds),
      ("Settings", previous.Settings, current.Settings),
      ("Environment", previous.Environment, current.Environment),
    ];
    var differences = compared
        .Where(x => x.Previous.Except(x.Current).Any() || x.Current.Except(x.Previous).Any())
        .Select(x => $"  {x.Name}: {Difference(x.Previous.Except(x.Current), x.Current.Except(x.Previous))}")
        .ToList();
    if (!keys.Contains(previous.Keys[0]))
      differences.Add($"  Keys: {Difference([Format.Compact(previous.Keys[0])], [])}");
    if (differences.Count == 0)
      return;
    throw new ArgumentException(
        $"""
        --resume continues execution {previous.Stamp}, which ran differently.
        {string.Join(Environment.NewLine, differences)}
        Use the same command, or drop --resume to start a new execution.
        """);
  }

  static string Difference(IEnumerable<string> onlyThere, IEnumerable<string> onlyHere)
  {
    IEnumerable<string> parts =
    [
      .. onlyThere.Any() ? [$"only in that execution: {string.Join("; ", onlyThere)}"] : Array.Empty<string>(),
      .. onlyHere.Any() ? [$"only in this command: {string.Join("; ", onlyHere)}"] : Array.Empty<string>(),
    ];
    return string.Join(". ", parts) + ".";
  }

  static string Text(int value) =>
      value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
