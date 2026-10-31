using System.Globalization;
using System.Text.Json;
using Transactions.Benchmark.Measurement;

namespace Transactions.Benchmark.Reporting;

public static class RunStatus
{
  public const string Ok = "ok";
  public const string Failed = "failed";
  public const string TimedOut = "timed-out";
}

public sealed record PhaseResult(string Name, double Seconds, long Rows);

public sealed record RoleResult
{
  public required string Name { get; init; }
  public required int Workers { get; init; }
  public required bool IsPrimary { get; init; }
  public required long Operations { get; init; }
  public required double OperationsPerSecond { get; init; }
  public required long Commits { get; init; }
  public required long Conflicts { get; init; }
  public required double ConflictPercent { get; init; }
  public required long Rollbacks { get; init; }
  public required long Rows { get; init; }
  public required double RowsPerSecond { get; init; }
  public required long Anomalies { get; init; }
  public required LatencySummary Latency { get; init; }
}

/// <summary>
/// The outcome of one engine, in one scenario, at one worker count and key count.
/// </summary>
public sealed record RunResult
{
  public required string ScenarioId { get; init; }
  public required string EngineId { get; init; }
  public required int Workers { get; init; }
  public required long Keys { get; init; }
  public required string Status { get; init; }

  /// <summary>
  /// The execution that measured this run. Only merged reports set it.
  /// </summary>
  public string? Stamp { get; init; }

  public string? Error { get; init; }
  public bool InvariantHolds { get; init; }
  public IReadOnlyList<string> FailedChecks { get; init; } = [];
  public IReadOnlyList<string> EngineSettings { get; init; } = [];
  public double JobSeconds { get; init; }
  public IReadOnlyList<RoleResult> Roles { get; init; } = [];
  public IReadOnlyList<PhaseResult> Phases { get; init; } = [];
  public double LoadSeconds { get; init; }
  public double SettleAfterLoadSeconds { get; init; }
  public double CloseSeconds { get; init; }
  public double ReopenSeconds { get; init; }
  public double FinalSettleSeconds { get; init; }
  public long StorageBytes { get; init; }
  public long PeakWorkingSetBytes { get; init; }
  public double CpuCores { get; init; }
  public double? CpuMicrosecondsPerOperation { get; init; }
  public double GcPauseMilliseconds { get; init; }
  public long? DiskWriteBytes { get; init; }
  public double? DiskWriteBytesPerCommit { get; init; }
  public long? DiskReadBytes { get; init; }

  public RoleResult? Primary => Roles.FirstOrDefault(x => x.IsPrimary);
}

/// <summary>
/// An execution that measured some runs of a merged report.
/// </summary>
public sealed record RunSource(string Stamp, IReadOnlyList<string> Environment);

/// <summary>
/// A whole benchmark execution or a merge of several, the file reports are rendered from.
/// </summary>
public sealed record BenchmarkReport
{
  public required string Stamp { get; init; }
  public required string Preset { get; init; }
  public required IReadOnlyList<long> Keys { get; init; }
  public required IReadOnlyList<int> Workers { get; init; }
  public required IReadOnlyList<string> EngineIds { get; init; }
  public required IReadOnlyList<string> ScenarioIds { get; init; }
  public required int ValueSize { get; init; }
  public required double Scale { get; init; }
  public required double ZipfTheta { get; init; }
  public required int LargeTransactionKeys { get; init; }
  public required bool RetryBackoff { get; init; }
  public required int Seed { get; init; }
  public required IReadOnlyList<string> Environment { get; init; }
  public required IReadOnlyList<RunResult> Runs { get; init; }

  /// <summary>
  /// The settings runs must share to be merged into one report.
  /// </summary>
  public IReadOnlyList<string> Settings { get; init; } = [];

  /// <summary>
  /// The runs an unfinished execution still expects. Zero in merged reports.
  /// </summary>
  public int PlannedRuns { get; init; }

  /// <summary>
  /// The executions behind a merged report, oldest first. Empty otherwise.
  /// </summary>
  public IReadOnlyList<RunSource> Sources { get; init; } = [];
}

public static class ResultFiles
{
  static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

  public static void Write<T>(string path, T value)
  {
    var temporary = path + ".tmp";
    File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
    File.Move(temporary, path, overwrite: true);
  }

  public static T Read<T>(string path) =>
      JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
          ?? throw new InvalidOperationException($"Could not read {path}.");
}

public static class Format
{
  public static string Compact(double value)
  {
    var magnitude = Math.Abs(value);
    return magnitude switch
    {
      >= 1e9 => Number(value / 1e9) + "B",
      >= 1e6 => Number(value / 1e6) + "M",
      >= 1e4 => Number(value / 1e3) + "K",
      _ => value.ToString(magnitude >= 100 || magnitude == Math.Floor(magnitude) ? "#,0" : "0.0", CultureInfo.InvariantCulture)
    };
  }

  static string Number(double value) =>
      value.ToString(Math.Abs(value) >= 100 ? "0" : Math.Abs(value) >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture);

  public static string Count(long value) =>
      value.ToString("#,0", CultureInfo.InvariantCulture);

  public static string Percent(double value) =>
      value.ToString(value is > 0 and < 10 ? "0.0#" : "0", CultureInfo.InvariantCulture) + "%";

  public static string Microseconds(double value) =>
      value switch
      {
        >= 1_000_000 => (value / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + " s",
        >= 1_000 => (value / 1_000).ToString(value >= 100_000 ? "0" : "0.##", CultureInfo.InvariantCulture) + " ms",
        _ => value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " µs"
      };

  public static string Seconds(double value) =>
      value >= 100
          ? value.ToString("0", CultureInfo.InvariantCulture) + " s"
          : value >= 1
              ? value.ToString("0.0#", CultureInfo.InvariantCulture) + " s"
              : (value * 1_000).ToString("0", CultureInfo.InvariantCulture) + " ms";

  public static string Bytes(double value)
  {
    string[] units = ["B", "KB", "MB", "GB", "TB"];
    var unit = 0;
    while (value >= 1024 && unit < units.Length - 1)
    {
      value /= 1024;
      unit++;
    }
    return value.ToString(value >= 100 || unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
  }

  public static string OptionalBytes(double? value) =>
      value.HasValue ? Bytes(value.Value) : "n/a";
}
