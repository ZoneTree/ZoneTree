using System.Globalization;

namespace Transactions.Benchmark;

public sealed record BenchmarkOptions
{
  public string Preset { get; init; } = "standard";

  public IReadOnlyList<string> Scenarios { get; init; } = ["all"];

  public IReadOnlyList<string> Engines { get; init; } = ["all"];

  public IReadOnlyList<int> Workers { get; init; } = [1, 4, 16];

  public IReadOnlyList<long> Keys { get; init; } = [100_000];

  public int ValueSize { get; init; } = 100;

  /// <summary>
  /// Multiplies the job of every scenario.
  /// </summary>
  public double Scale { get; init; } = 1;

  public double ZipfTheta { get; init; } = 0.99;

  /// <summary>
  /// The rows of the large transaction. Zero means the run's key count.
  /// </summary>
  public int LargeTransactionKeys { get; init; }

  public int Seed { get; init; } = 42;

  public bool RetryBackoff { get; init; }

  public int TimeoutSeconds { get; init; } = 600;

  public string DataDirectory { get; init; } = "";

  public string OutputDirectory { get; init; } = "";

  public bool Clean { get; init; }

  public bool CleanData { get; init; }

  public bool CleanResults { get; init; }

  public bool KeepData { get; init; }

  public bool UpdateLatest { get; init; }

  public bool ListScenarios { get; init; }

  public bool ShowHelp { get; init; }

  public string RenderReport { get; init; } = "";

  public bool RenderLatest { get; init; }

  public int ZoneTreeMutableSegmentMaxItemCount { get; init; } = 250_000;

  public int ZoneTreeSparseArrayStepSize { get; init; } = 16;

  public int ZoneTreeKeyCacheSize { get; init; } = 1_024;

  public int ZoneTreeValueCacheSize { get; init; } = 1_024;

  public int RocksDbWriteBufferMb { get; init; } = 256;

  public int RocksDbMaxWriteBuffers { get; init; } = 4;

  public int RocksDbBlockCacheMb { get; init; } = 1_024;

  public long RocksDbLockTimeoutMs { get; init; } = 1_000;

  public int SqliteCacheMb { get; init; } = 1_024;

  public int SqliteMmapMb { get; init; } = 1_024;

  public bool ChildRun { get; init; }

  public string ResultFile { get; init; } = "";

  public static string HelpText =>
      """
      Transaction benchmark: ZoneTree, RocksDB and SQLite across many transaction use cases.

      Selection:
        --list-scenarios             Print the scenario catalog and exit.
        --scenario <list>            all, ids, or category:<name>. Comma separated. Default: all.
        --engine <list>              all, zonetree, rocksdb, sqlite, or variant ids. Default: all.
        --preset <name>              smoke, standard or full. Default: standard.
                                       smoke:    1K keys, 4 workers
                                       standard: 100K keys, 1,4,16 workers
                                       full:     100K,1M keys, 1,2,4,8,16,32 workers

      Workload (overrides the preset):
        --workers <list>             Worker threads per run, for example 1,4,16.
        --keys <list>                Loaded keys per run, for example 100K,1M. Minimum: 1K.
                                     Jobs grow with the keys, so this sets how long runs take.
                                     Each key count gets its own report.
        --value-size <bytes>         Value size. Default: 100. Minimum: 16.
        --scale <factor>             Multiplies every scenario's job, for example 0.1.
        --zipf-theta <value>         Skew of the zipf scenarios, below 1. Default: 0.99.
        --large-txn-keys <count>     Keys in the large-txn scenario. Default: the --keys value.
        --retry-backoff              Wait with exponential backoff after a conflict.
        --seed <value>               Seed of the workers. Default: 42.

      Execution and output:
        --timeout-seconds <value>    Kill a run after this time. 0 disables it. Default: 600.
        --data <directory>           Data directory. Default: benchmarks/transactions/out/data.
        --output <directory>         Results directory. Default: benchmarks/transactions/out/results.
        --clean, --clean-data, --clean-results
                                     Delete data and/or results first.
        --keep-data                  Keep each run's database files, to inspect them.
        --update-latest              Merge each finished run into reference/<os>/<preset>/<keys>,
                                     replacing its previous result, and rewrite that report.
        --render-latest              Rewrite the reports of every key count in
                                     reference/<os>/<preset> from their summary.json files,
                                     without running anything.
        --render-report <directory>  The same for a report directory, or the key counts under it.

      Engine tuning:
        --zonetree-mutable-segment-max-item-count <count>   Default: 250K.
        --zonetree-sparse-array-step-size <count>           Default: 16.
        --zonetree-key-cache-size <count>                   Default: 1024.
        --zonetree-value-cache-size <count>                 Default: 1024.
        --rocksdb-write-buffer-mb <mb>                      Default: 256.
        --rocksdb-max-write-buffers <count>                 Default: 4.
        --rocksdb-block-cache-mb <mb>                       Default: 1024.
        --rocksdb-lock-timeout-ms <ms>                      Default: 1000.
        --sqlite-cache-mb <mb>                              Default: 1024.
        --sqlite-mmap-mb <mb>                               Default: 1024.
      """;

  public static BenchmarkOptions Parse(string[] args)
  {
    var options = new BenchmarkOptions();
    var preset = FindPreset(args);
    if (preset != null)
      options = ApplyPreset(options, preset);

    for (var i = 0; i < args.Length; i++)
    {
      var name = args[i];
      string Next()
      {
        if (++i >= args.Length)
          throw new ArgumentException($"{name} needs a value.");
        return args[i];
      }

      options = name switch
      {
        "--help" or "-h" => options with { ShowHelp = true },
        "--list-scenarios" => options with { ListScenarios = true },
        "--preset" => Skip(options, Next()),
        "--scenario" => options with { Scenarios = ParseList(Next()) },
        "--engine" => options with { Engines = ParseList(Next()) },
        "--workers" => options with { Workers = ParseList(Next()).Select(x => (int)ParseCount(x)).ToArray() },
        "--keys" => options with { Keys = ParseList(Next()).Select(ParseCount).ToArray() },
        "--value-size" => options with { ValueSize = (int)ParseCount(Next()) },
        "--scale" => options with { Scale = ParseDouble(Next()) },
        "--zipf-theta" => options with { ZipfTheta = ParseDouble(Next()) },
        "--large-txn-keys" => options with { LargeTransactionKeys = (int)ParseCount(Next()) },
        "--retry-backoff" => options with { RetryBackoff = true },
        "--seed" => options with { Seed = int.Parse(Next(), CultureInfo.InvariantCulture) },
        "--timeout-seconds" => options with { TimeoutSeconds = int.Parse(Next(), CultureInfo.InvariantCulture) },
        "--data" => options with { DataDirectory = Next() },
        "--output" => options with { OutputDirectory = Next() },
        "--clean" => options with { Clean = true },
        "--clean-data" => options with { CleanData = true },
        "--clean-results" => options with { CleanResults = true },
        "--keep-data" => options with { KeepData = true },
        "--update-latest" => options with { UpdateLatest = true },
        "--render-report" => options with { RenderReport = Next() },
        "--render-latest" => options with { RenderLatest = true },
        "--zonetree-mutable-segment-max-item-count" => options with { ZoneTreeMutableSegmentMaxItemCount = (int)ParseCount(Next()) },
        "--zonetree-sparse-array-step-size" => options with { ZoneTreeSparseArrayStepSize = (int)ParseCount(Next()) },
        "--zonetree-key-cache-size" => options with { ZoneTreeKeyCacheSize = (int)ParseCount(Next()) },
        "--zonetree-value-cache-size" => options with { ZoneTreeValueCacheSize = (int)ParseCount(Next()) },
        "--rocksdb-write-buffer-mb" => options with { RocksDbWriteBufferMb = (int)ParseCount(Next()) },
        "--rocksdb-max-write-buffers" => options with { RocksDbMaxWriteBuffers = (int)ParseCount(Next()) },
        "--rocksdb-block-cache-mb" => options with { RocksDbBlockCacheMb = (int)ParseCount(Next()) },
        "--rocksdb-lock-timeout-ms" => options with { RocksDbLockTimeoutMs = ParseCount(Next()) },
        "--sqlite-cache-mb" => options with { SqliteCacheMb = (int)ParseCount(Next()) },
        "--sqlite-mmap-mb" => options with { SqliteMmapMb = (int)ParseCount(Next()) },
        "--child-run" => options with { ChildRun = true },
        "--result-file" => options with { ResultFile = Next() },
        _ => throw new ArgumentException($"Unknown option '{name}'. Use --help.")
      };
    }
    options.Validate();
    return options;
  }

  static BenchmarkOptions Skip(BenchmarkOptions options, string _) => options;

  static string? FindPreset(string[] args)
  {
    for (var i = 0; i < args.Length - 1; i++)
    {
      if (args[i] == "--preset")
        return args[i + 1];
    }
    return null;
  }

  static BenchmarkOptions ApplyPreset(BenchmarkOptions options, string preset) => preset switch
  {
    "smoke" => options with { Preset = preset, Keys = [1_000], Workers = [4] },
    "standard" => options with { Preset = preset },
    "full" => options with { Preset = preset, Keys = [100_000, 1_000_000], Workers = [1, 2, 4, 8, 16, 32], TimeoutSeconds = 1_800 },
    _ => throw new ArgumentException($"Unknown preset '{preset}'. Use smoke, standard or full.")
  };

  void Validate()
  {
    if (ValueSize < 16)
      throw new ArgumentException("--value-size must be at least 16: a value starts with its 8 byte counter.");
    if (ZipfTheta <= 0 || ZipfTheta >= 1)
      throw new ArgumentException("--zipf-theta must be between 0 and 1.");
    if (Workers.Count == 0 || Workers.Any(x => x < 1))
      throw new ArgumentException("--workers must list positive counts.");
    if (Keys.Count == 0 || Keys.Any(x => x < 1_000))
      throw new ArgumentException("--keys must list counts of at least 1000.");
    if (Scale <= 0)
      throw new ArgumentException("--scale must be positive.");
    if (LargeTransactionKeys < 0)
      throw new ArgumentException("--large-txn-keys must not be negative.");
  }

  /// <summary>
  /// The arguments of the process that runs one engine in one scenario.
  /// </summary>
  public IEnumerable<string> ToChildArguments(string scenarioId, string engineId, int workers, long keys, string resultFile)
  {
    yield return "--child-run";
    yield return "--result-file";
    yield return resultFile;
    yield return "--scenario";
    yield return scenarioId;
    yield return "--engine";
    yield return engineId;
    yield return "--workers";
    yield return Text(workers);
    yield return "--keys";
    yield return Text(keys);
    yield return "--data";
    yield return DataDirectory;
    yield return "--output";
    yield return OutputDirectory;
    if (KeepData)
      yield return "--keep-data";
    foreach (var (name, value) in MeasuredSettings())
    {
      yield return name;
      if (value != null)
        yield return value;
    }
  }

  /// <summary>
  /// The settings runs must share for a report to compare them across executions.
  /// </summary>
  public IReadOnlyList<string> ComparisonSettings()
  {
    var tiered = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation");
    return
    [
      .. MeasuredSettings().Select(x => x.Value == null ? x.Name : $"{x.Name} {x.Value}"),
      $"DOTNET_TieredCompilation {(string.IsNullOrEmpty(tiered) ? "unset" : tiered)}",
    ];
  }

  /// <summary>
  /// The options that decide what a run measures. A flag has no value.
  /// </summary>
  IEnumerable<(string Name, string? Value)> MeasuredSettings()
  {
    yield return ("--value-size", Text(ValueSize));
    yield return ("--scale", Text(Scale));
    yield return ("--zipf-theta", Text(ZipfTheta));
    yield return ("--large-txn-keys", Text(LargeTransactionKeys));
    yield return ("--seed", Text(Seed));
    if (RetryBackoff)
      yield return ("--retry-backoff", null);
    yield return ("--zonetree-mutable-segment-max-item-count", Text(ZoneTreeMutableSegmentMaxItemCount));
    yield return ("--zonetree-sparse-array-step-size", Text(ZoneTreeSparseArrayStepSize));
    yield return ("--zonetree-key-cache-size", Text(ZoneTreeKeyCacheSize));
    yield return ("--zonetree-value-cache-size", Text(ZoneTreeValueCacheSize));
    yield return ("--rocksdb-write-buffer-mb", Text(RocksDbWriteBufferMb));
    yield return ("--rocksdb-max-write-buffers", Text(RocksDbMaxWriteBuffers));
    yield return ("--rocksdb-block-cache-mb", Text(RocksDbBlockCacheMb));
    yield return ("--rocksdb-lock-timeout-ms", Text(RocksDbLockTimeoutMs));
    yield return ("--sqlite-cache-mb", Text(SqliteCacheMb));
    yield return ("--sqlite-mmap-mb", Text(SqliteMmapMb));
  }

  static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

  static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);

  static string[] ParseList(string value) =>
      value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

  static double ParseDouble(string value) =>
      double.Parse(value, CultureInfo.InvariantCulture);

  static long ParseCount(string value)
  {
    var multiplier = 1L;
    if (value.EndsWith('K') || value.EndsWith('k'))
      multiplier = 1_000;
    else if (value.EndsWith('M') || value.EndsWith('m'))
      multiplier = 1_000_000;
    var digits = multiplier == 1 ? value : value[..^1];
    return long.Parse(digits.Replace("_", ""), CultureInfo.InvariantCulture) * multiplier;
  }
}
